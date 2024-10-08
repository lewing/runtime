// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// File: stubhelpers.cpp
//

#include "common.h"

#include "mlinfo.h"
#include "stubhelpers.h"
#include "jitinterface.h"
#include "dllimport.h"
#include "fieldmarshaler.h"
#include "comdelegate.h"
#include "eventtrace.h"
#include "comdatetime.h"
#include "gcheaputilities.h"
#include "interoputil.h"

#ifdef FEATURE_COMINTEROP
#include <oletls.h>
#include "olecontexthelpers.h"
#include "runtimecallablewrapper.h"
#include "comcallablewrapper.h"
#include "clrtocomcall.h"
#include "cominterfacemarshaler.h"
#endif

#ifdef VERIFY_HEAP

CQuickArray<StubHelpers::ByrefValidationEntry> StubHelpers::s_ByrefValidationEntries;
SIZE_T StubHelpers::s_ByrefValidationIndex = 0;
CrstStatic StubHelpers::s_ByrefValidationLock;

// static
void StubHelpers::Init()
{
    WRAPPER_NO_CONTRACT;
    s_ByrefValidationLock.Init(CrstPinnedByrefValidation);
}

// static
void StubHelpers::ValidateObjectInternal(Object *pObjUNSAFE, BOOL fValidateNextObj)
{
	CONTRACTL
	{
	NOTHROW;
	GC_NOTRIGGER;
	MODE_ANY;
}
	CONTRACTL_END;

	_ASSERTE(GCHeapUtilities::GetGCHeap()->RuntimeStructuresValid());

	// validate the object - there's no need to validate next object's
	// header since we validate the next object explicitly below
	if (pObjUNSAFE)
	{
		pObjUNSAFE->Validate(/*bDeep=*/ TRUE, /*bVerifyNextHeader=*/ FALSE, /*bVerifySyncBlock=*/ TRUE);
	}

	// and the next object as required
	if (fValidateNextObj)
	{
		Object *nextObj = GCHeapUtilities::GetGCHeap()->NextObj(pObjUNSAFE);
		if (nextObj != NULL)
		{
			// Note that the MethodTable of the object (i.e. the pointer at offset 0) can change from
			// g_pFreeObjectMethodTable to NULL, from NULL to <legal-value>, or possibly also from
			// g_pFreeObjectMethodTable to <legal-value> concurrently while executing this function.
			// Once <legal-value> is seen, we believe that the object should pass the Validate check.
			// We have to be careful and read the pointer only once to avoid "phantom reads".
			MethodTable *pMT = VolatileLoad(nextObj->GetMethodTablePtr());
			if (pMT != NULL && pMT != g_pFreeObjectMethodTable)
			{
				// do *not* verify the next object's syncblock - the next object is not guaranteed to
				// be "alive" so the finalizer thread may have already released its syncblock
				nextObj->Validate(/*bDeep=*/ TRUE, /*bVerifyNextHeader=*/ FALSE, /*bVerifySyncBlock=*/ FALSE);
			}
		}
	}
}

// static
MethodDesc *StubHelpers::ResolveInteropMethod(Object *pThisUNSAFE, MethodDesc *pMD)
{
    WRAPPER_NO_CONTRACT;

    if (pMD == NULL && pThisUNSAFE != NULL)
    {
        // if this is a call via delegate, get its Invoke method
        MethodTable *pMT = pThisUNSAFE->GetMethodTable();

        _ASSERTE(pMT->IsDelegate());
        return ((DelegateEEClass *)pMT->GetClass())->GetInvokeMethod();
    }
    return pMD;
}

// static
void StubHelpers::FormatValidationMessage(MethodDesc *pMD, SString &ssErrorString)
{
    CONTRACTL
    {
        THROWS;
        GC_NOTRIGGER;
        MODE_ANY;
    }
    CONTRACTL_END;

    ssErrorString.Append(W("Detected managed heap corruption, likely culprit is interop call through "));

    if (pMD == NULL)
    {
        // the only case where we don't have interop MD is CALLI
        ssErrorString.Append(W("CALLI."));
    }
    else
    {
        ssErrorString.Append(W("method '"));

        StackSString ssClassName;
        pMD->GetMethodTable()->_GetFullyQualifiedNameForClass(ssClassName);

        ssErrorString.Append(ssClassName);
        ssErrorString.Append(NAMESPACE_SEPARATOR_CHAR);
        ssErrorString.AppendUTF8(pMD->GetName());

        ssErrorString.Append(W("'."));
    }
}

// static
void StubHelpers::ProcessByrefValidationList()
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
        MODE_ANY;
    }
    CONTRACTL_END;

    StackSString errorString;
    ByrefValidationEntry entry = { NULL, NULL };

    EX_TRY
    {
        AVInRuntimeImplOkayHolder AVOkay;

        // Process all byref validation entries we have saved since the last GC. Note that EE is suspended at
        // this point so we don't have to take locks and we can safely call code:GCHeap.GetContainingObject.
        for (SIZE_T i = 0; i < s_ByrefValidationIndex; i++)
        {
            entry = s_ByrefValidationEntries[i];

            Object *pObjUNSAFE = GCHeapUtilities::GetGCHeap()->GetContainingObject(entry.pByref, false);
            ValidateObjectInternal(pObjUNSAFE, TRUE);
        }
    }
    EX_CATCH
    {
        EX_TRY
        {
            FormatValidationMessage(entry.pMD, errorString);
            EEPOLICY_HANDLE_FATAL_ERROR_WITH_MESSAGE(COR_E_EXECUTIONENGINE, errorString.GetUnicode());
        }
        EX_CATCH
        {
            EEPOLICY_HANDLE_FATAL_ERROR(COR_E_EXECUTIONENGINE);
        }
        EX_END_CATCH_UNREACHABLE;
    }
    EX_END_CATCH_UNREACHABLE;

    s_ByrefValidationIndex = 0;
}

#endif // VERIFY_HEAP

#ifdef FEATURE_COMINTEROP

FORCEINLINE static void GetCOMIPFromRCW_ClearFP()
{
    LIMITED_METHOD_CONTRACT;

#ifdef TARGET_X86
    // As per ASURT 146699 we need to clear FP state before calling to COM
    // the following sequence was previously generated to compiled ML stubs
    // and is faster than _clearfp().
    __asm
    {
        fnstsw ax
        and    eax, 0x3F
        jz     NoNeedToClear
        fnclex
NoNeedToClear:
    }
#endif // TARGET_X86
}

FORCEINLINE static SOleTlsData *GetOrCreateOleTlsData()
{
    LIMITED_METHOD_CONTRACT;

    SOleTlsData *pOleTlsData;
#ifdef TARGET_X86
    // This saves 1 memory instruction over NtCurretTeb()->ReservedForOle because
    // NtCurrentTeb() reads _TEB.NtTib.Self which is the same as what FS:0 already
    // points to.
    pOleTlsData = (SOleTlsData *)(ULONG_PTR)__readfsdword(offsetof(TEB, ReservedForOle));
#else // TARGET_X86
    pOleTlsData = (SOleTlsData *)NtCurrentTeb()->ReservedForOle;
#endif // TARGET_X86
    if (pOleTlsData == NULL)
    {
        pOleTlsData = (SOleTlsData *)SetupOleContext();
    }
    return pOleTlsData;
}

FORCEINLINE static IUnknown *GetCOMIPFromRCW_GetIUnknownFromRCWCache(RCW *pRCW, MethodTable * pItfMT)
{
    LIMITED_METHOD_CONTRACT;

    // The code in this helper is the "fast path" that used to be generated directly
    // to compiled ML stubs. The idea is to aim for an efficient RCW cache hit.
    SOleTlsData * pOleTlsData = GetOrCreateOleTlsData();

    // test for free-threaded after testing for context match to optimize for apartment-bound objects
    if (pOleTlsData->pCurrentCtx == pRCW->GetWrapperCtxCookie() || pRCW->IsFreeThreaded())
    {
        for (int i = 0; i < INTERFACE_ENTRY_CACHE_SIZE; i++)
        {
            if (pRCW->m_aInterfaceEntries[i].m_pMT == pItfMT)
            {
                return pRCW->m_aInterfaceEntries[i].m_pUnknown;
            }
        }
    }

    return NULL;
}

FORCEINLINE static void *GetCOMIPFromRCW_GetTarget(IUnknown *pUnk, CLRToCOMCallInfo *pComInfo)
{
    LIMITED_METHOD_CONTRACT;


    LPVOID *lpVtbl = *(LPVOID **)pUnk;
    return lpVtbl[pComInfo->m_cachedComSlot];
}

NOINLINE static IUnknown* GetCOMIPFromRCWHelper(LPVOID pFCall, OBJECTREF pSrc, MethodDesc* pMD, void **ppTarget)
{
    FC_INNER_PROLOG(pFCall);

    IUnknown *pIntf = NULL;

    // This is only called in IL stubs which are in CER, so we don't need to worry about ThreadAbort
    HELPER_METHOD_FRAME_BEGIN_RET_ATTRIB_1(Frame::FRAME_ATTR_NO_THREAD_ABORT|Frame::FRAME_ATTR_EXACT_DEPTH|Frame::FRAME_ATTR_CAPTURE_DEPTH_2, pSrc);

    SafeComHolder<IUnknown> pRetUnk;

    CLRToCOMCallInfo *pComInfo = CLRToCOMCallInfo::FromMethodDesc(pMD);
    pRetUnk = ComObject::GetComIPFromRCWThrowing(&pSrc, pComInfo->m_pInterfaceMT);

    *ppTarget = GetCOMIPFromRCW_GetTarget(pRetUnk, pComInfo);
    _ASSERTE(*ppTarget != NULL);

    GetCOMIPFromRCW_ClearFP();

    pIntf = pRetUnk.Extract();

    // No exception will be thrown here (including thread abort as it is delayed in IL stubs)
    HELPER_METHOD_FRAME_END();

    FC_INNER_EPILOG();
    return pIntf;
}

//==================================================================================================================
// The GetCOMIPFromRCW helper exists in four specialized versions to optimize CLR->COM perf. Please be careful when
// changing this code as one of these methods is executed as part of every CLR->COM call so every instruction counts.
//==================================================================================================================


#include <optsmallperfcritical.h>

// This helper can handle any CLR->COM call, it supports hosting,
// and clears FP state on x86 for compatibility with VB6.
FCIMPL4(IUnknown*, StubHelpers::GetCOMIPFromRCW, Object* pSrcUNSAFE, MethodDesc* pMD, void **ppTarget, CLR_BOOL* pfNeedsRelease)
{
    CONTRACTL
    {
        FCALL_CHECK;
        PRECONDITION(pMD->IsCLRToCOMCall() || pMD->IsEEImpl());
    }
    CONTRACTL_END;

    OBJECTREF pSrc = ObjectToOBJECTREF(pSrcUNSAFE);
    *pfNeedsRelease = false;

    CLRToCOMCallInfo *pComInfo = CLRToCOMCallInfo::FromMethodDesc(pMD);
    RCW *pRCW = pSrc->PassiveGetSyncBlock()->GetInteropInfoNoCreate()->GetRawRCW();
    if (pRCW != NULL)
    {

        IUnknown * pUnk = GetCOMIPFromRCW_GetIUnknownFromRCWCache(pRCW, pComInfo->m_pInterfaceMT);
        if (pUnk != NULL)
        {
            *ppTarget = GetCOMIPFromRCW_GetTarget(pUnk, pComInfo);
            if (*ppTarget != NULL)
            {
                GetCOMIPFromRCW_ClearFP();
                return pUnk;
            }
        }
    }

    /* if we didn't find the COM interface pointer in the cache we will have to erect an HMF */
    *pfNeedsRelease = true;
    FC_INNER_RETURN(IUnknown*, GetCOMIPFromRCWHelper(StubHelpers::GetCOMIPFromRCW, pSrc, pMD, ppTarget));
}
FCIMPLEND

#include <optdefault.h>

extern "C" void QCALLTYPE ObjectMarshaler_ConvertToNative(QCall::ObjectHandleOnStack pSrcUNSAFE, VARIANT* pDest)
{
    QCALL_CONTRACT;

    BEGIN_QCALL;

    GCX_COOP();

    OBJECTREF pSrc = pSrcUNSAFE.Get();
    GCPROTECT_BEGIN(pSrc);

    if (pDest->vt & VT_BYREF)
    {
        OleVariant::MarshalOleRefVariantForObject(&pSrc, pDest);
    }
    else
    {
        OleVariant::MarshalOleVariantForObject(&pSrc, pDest);
    }

    GCPROTECT_END();

    END_QCALL;
}

extern "C" void QCALLTYPE ObjectMarshaler_ConvertToManaged(VARIANT* pSrc, QCall::ObjectHandleOnStack retObject)
{
    QCALL_CONTRACT;

    BEGIN_QCALL;

    GCX_COOP();

    OBJECTREF retVal = NULL;
    GCPROTECT_BEGIN(retVal);

    // The IL stub is going to call ObjectMarshaler.ClearNative() afterwards.
    // If it doesn't it's a bug in ILObjectMarshaler.
    OleVariant::MarshalObjectForOleVariant(pSrc, &retVal);
    retObject.Set(retVal);

    GCPROTECT_END();

    END_QCALL;
}

#include <optsmallperfcritical.h>
extern "C" IUnknown* QCALLTYPE InterfaceMarshaler_ConvertToNative(QCall::ObjectHandleOnStack pObjUNSAFE, MethodTable* pItfMT, MethodTable* pClsMT, DWORD dwFlags)
{
    QCALL_CONTRACT;

    IUnknown *pIntf = NULL;
    BEGIN_QCALL;

    // We're going to be making some COM calls, better initialize COM.
    EnsureComStarted();

    GCX_COOP();

    OBJECTREF pObj = pObjUNSAFE.Get();
    GCPROTECT_BEGIN(pObj);

    pIntf = MarshalObjectToInterface(&pObj, pItfMT, pClsMT, dwFlags);

    GCPROTECT_END();

    END_QCALL;

    return pIntf;
}

extern "C" void QCALLTYPE InterfaceMarshaler_ConvertToManaged(IUnknown** ppUnk, MethodTable* pItfMT, MethodTable* pClsMT, DWORD dwFlags, QCall::ObjectHandleOnStack retObject)
{
    QCALL_CONTRACT;

    BEGIN_QCALL;

    // We're going to be making some COM calls, better initialize COM.
    EnsureComStarted();

    GCX_COOP();

    OBJECTREF pObj = NULL;
    GCPROTECT_BEGIN(pObj);

    UnmarshalObjectFromInterface(&pObj, ppUnk, pItfMT, pClsMT, dwFlags);
    retObject.Set(pObj);

    GCPROTECT_END();

    END_QCALL;
}
#include <optdefault.h>

#endif // FEATURE_COMINTEROP

FCIMPL0(void, StubHelpers::ClearLastError)
{
    FCALL_CONTRACT;

    ::SetLastError(0);
}
FCIMPLEND

FCIMPL1(void*, StubHelpers::GetDelegateTarget, DelegateObject *pThisUNSAFE)
{
    PCODE pEntryPoint = (PCODE)NULL;

#ifdef _DEBUG
    BEGIN_PRESERVE_LAST_ERROR;
#endif

    CONTRACTL
    {
        FCALL_CHECK;
        PRECONDITION(CheckPointer(pThisUNSAFE));
    }
    CONTRACTL_END;

    DELEGATEREF orefThis = (DELEGATEREF)ObjectToOBJECTREF(pThisUNSAFE);

#if defined(HOST_64BIT)
    UINT_PTR target = (UINT_PTR)orefThis->GetMethodPtrAux();

    // See code:GenericPInvokeCalliHelper
    // The lowest bit is used to distinguish between MD and target on 64-bit.
    target = (target << 1) | 1;
#endif // HOST_64BIT

    pEntryPoint = orefThis->GetMethodPtrAux();

#ifdef _DEBUG
    END_PRESERVE_LAST_ERROR;
#endif

    return (PVOID)pEntryPoint;
}
FCIMPLEND

#include <optsmallperfcritical.h>
FCIMPL2(FC_BOOL_RET, StubHelpers::TryGetStringTrailByte, StringObject* thisRefUNSAFE, UINT8 *pbData)
{
    FCALL_CONTRACT;

    STRINGREF thisRef = ObjectToSTRINGREF(thisRefUNSAFE);
    FC_RETURN_BOOL(thisRef->GetTrailByte(pbData));
}
FCIMPLEND
#include <optdefault.h>

extern "C" void QCALLTYPE StubHelpers_SetStringTrailByte(QCall::StringHandleOnStack str, UINT8 bData)
{
    QCALL_CONTRACT;

    BEGIN_QCALL;

    GCX_COOP();
    str.Get()->SetTrailByte(bData);

    END_QCALL;
}

extern "C" void QCALLTYPE StubHelpers_ThrowInteropParamException(INT resID, INT paramIdx)
{
    QCALL_CONTRACT;

    BEGIN_QCALL;
    ::ThrowInteropParamException(resID, paramIdx);
    END_QCALL;
}

#ifdef PROFILING_SUPPORTED
extern "C" void* QCALLTYPE StubHelpers_ProfilerBeginTransitionCallback(MethodDesc* pTargetMD)
{
    BEGIN_PRESERVE_LAST_ERROR;

    QCALL_CONTRACT;

    BEGIN_QCALL;

    ProfilerManagedToUnmanagedTransitionMD(pTargetMD, COR_PRF_TRANSITION_CALL);

    END_QCALL;

    END_PRESERVE_LAST_ERROR;

    return pTargetMD;
}

extern "C" void QCALLTYPE StubHelpers_ProfilerEndTransitionCallback(MethodDesc* pTargetMD)
{
    BEGIN_PRESERVE_LAST_ERROR;

    QCALL_CONTRACT;

    BEGIN_QCALL;

    ProfilerUnmanagedToManagedTransitionMD(pTargetMD, COR_PRF_TRANSITION_RETURN);

    END_QCALL;

    END_PRESERVE_LAST_ERROR;
}
#endif // PROFILING_SUPPORTED

FCIMPL1(Object*, StubHelpers::GetHRExceptionObject, HRESULT hr)
{
    FCALL_CONTRACT;

    OBJECTREF oThrowable = NULL;

    HELPER_METHOD_FRAME_BEGIN_RET_1(oThrowable);
    {
        // GetExceptionForHR uses equivalant logic as COMPlusThrowHR
        GetExceptionForHR(hr, &oThrowable);
    }
    HELPER_METHOD_FRAME_END();

    return OBJECTREFToObject(oThrowable);
}
FCIMPLEND

#ifdef FEATURE_COMINTEROP
FCIMPL3(Object*, StubHelpers::GetCOMHRExceptionObject, HRESULT hr, MethodDesc *pMD, Object *unsafe_pThis)
{
    FCALL_CONTRACT;

    OBJECTREF oThrowable = NULL;

    // get 'this'
    OBJECTREF oref = ObjectToOBJECTREF(unsafe_pThis);

    HELPER_METHOD_FRAME_BEGIN_RET_2(oref, oThrowable);
    {
        IErrorInfo *pErrInfo = NULL;

        if (pMD != NULL)
        {
            // Retrieve the interface method table.
            MethodTable *pItfMT = CLRToCOMCallInfo::FromMethodDesc(pMD)->m_pInterfaceMT;

            // Get IUnknown pointer for this interface on this object
            IUnknown* pUnk = ComObject::GetComIPFromRCW(&oref, pItfMT);
            if (pUnk != NULL)
            {
                // Check to see if the component supports error information for this interface.
                IID ItfIID;
                pItfMT->GetGuid(&ItfIID, TRUE);
                pErrInfo = GetSupportedErrorInfo(pUnk, ItfIID);

                DWORD cbRef = SafeRelease(pUnk);
                LogInteropRelease(pUnk, cbRef, "IUnk to QI for ISupportsErrorInfo");
            }
        }

        GetExceptionForHR(hr, pErrInfo, &oThrowable);
    }
    HELPER_METHOD_FRAME_END();

    return OBJECTREFToObject(oThrowable);
}
FCIMPLEND
#endif // FEATURE_COMINTEROP

FCIMPL1(Object*, StubHelpers::AllocateInternal, EnregisteredTypeHandle pRegisteredTypeHnd)
{
    FCALL_CONTRACT;

    TypeHandle typeHnd = TypeHandle::FromPtr(pRegisteredTypeHnd);
    OBJECTREF objRet = NULL;
    HELPER_METHOD_FRAME_BEGIN_RET_1(objRet);

    MethodTable* pMT = typeHnd.GetMethodTable();
    objRet = pMT->Allocate();

    HELPER_METHOD_FRAME_END();

    return OBJECTREFToObject(objRet);
}
FCIMPLEND

FCIMPL3(void, StubHelpers::MarshalToUnmanagedVaListInternal, va_list va, DWORD cbVaListSize, const VARARGS* pArgIterator)
{
    FCALL_CONTRACT;

    HELPER_METHOD_FRAME_BEGIN_0();
    VARARGS::MarshalToUnmanagedVaList(va, cbVaListSize, pArgIterator);
    HELPER_METHOD_FRAME_END();
}
FCIMPLEND

FCIMPL2(void, StubHelpers::MarshalToManagedVaListInternal, va_list va, VARARGS* pArgIterator)
{
    FCALL_CONTRACT;

    VARARGS::MarshalToManagedVaList(va, pArgIterator);
}
FCIMPLEND

FCIMPL3(void, StubHelpers::ValidateObject, Object *pObjUNSAFE, MethodDesc *pMD, Object *pThisUNSAFE)
{
    FCALL_CONTRACT;

#ifdef VERIFY_HEAP
    HELPER_METHOD_FRAME_BEGIN_0();

    StackSString errorString;
    EX_TRY
    {
        AVInRuntimeImplOkayHolder AVOkay;
		// don't validate the next object if a BGC is in progress.  we can race with background
	    // sweep which could make the next object a Free object underneath us if it's dead.
        ValidateObjectInternal(pObjUNSAFE, !(GCHeapUtilities::GetGCHeap()->IsConcurrentGCInProgress()));
    }
    EX_CATCH
    {
        FormatValidationMessage(ResolveInteropMethod(pThisUNSAFE, pMD), errorString);
        EEPOLICY_HANDLE_FATAL_ERROR_WITH_MESSAGE(COR_E_EXECUTIONENGINE, errorString.GetUnicode());
    }
    EX_END_CATCH_UNREACHABLE;

    HELPER_METHOD_FRAME_END();
#else // VERIFY_HEAP
    FCUnique(0xa3);
    UNREACHABLE_MSG("No validation support without VERIFY_HEAP");
#endif // VERIFY_HEAP
}
FCIMPLEND

FCIMPL3(void, StubHelpers::ValidateByref, void *pByref, MethodDesc *pMD, Object *pThisUNSAFE)
{
    FCALL_CONTRACT;

#ifdef VERIFY_HEAP
    // We cannot validate byrefs at this point as code:GCHeap.GetContainingObject could potentially race
    // with allocations on other threads. We'll just remember this byref along with the interop MD and
    // perform the validation on next GC (see code:StubHelpers.ProcessByrefValidationList).

    // Skip byref if is not pointing inside managed heap
    if (!GCHeapUtilities::GetGCHeap()->IsHeapPointer(pByref))
    {
        return;
    }
    ByrefValidationEntry entry;
    entry.pByref = pByref;
    entry.pMD = ResolveInteropMethod(pThisUNSAFE, pMD);

    HELPER_METHOD_FRAME_BEGIN_0();

    SIZE_T NumOfEntries = 0;
    {
        CrstHolder ch(&s_ByrefValidationLock);

        if (s_ByrefValidationIndex >= s_ByrefValidationEntries.Size())
        {
            // The validation list grows as necessary, for simplicity we never shrink it.
            SIZE_T newSize;
            if (!ClrSafeInt<SIZE_T>::multiply(s_ByrefValidationIndex, 2, newSize) ||
                !ClrSafeInt<SIZE_T>::addition(newSize, 1, newSize))
            {
                ThrowHR(COR_E_OVERFLOW);
            }

            s_ByrefValidationEntries.ReSizeThrows(newSize);
            _ASSERTE(s_ByrefValidationIndex < s_ByrefValidationEntries.Size());
        }

        s_ByrefValidationEntries[s_ByrefValidationIndex] = entry;
        NumOfEntries = ++s_ByrefValidationIndex;
    }

    if (NumOfEntries > BYREF_VALIDATION_LIST_MAX_SIZE)
    {
        // if the list is too big, trigger GC now
        GCHeapUtilities::GetGCHeap()->GarbageCollect(0);
    }

    HELPER_METHOD_FRAME_END();
#else // VERIFY_HEAP
    FCUnique(0xa4);
    UNREACHABLE_MSG("No validation support without VERIFY_HEAP");
#endif // VERIFY_HEAP
}
FCIMPLEND

FCIMPL0(void*, StubHelpers::GetStubContext)
{
    FCALL_CONTRACT;

    FCUnique(0xa0);
    UNREACHABLE_MSG_RET("This is a JIT intrinsic!");
}
FCIMPLEND

FCIMPL2(void, StubHelpers::LogPinnedArgument, MethodDesc *target, Object *pinnedArg)
{
    FCALL_CONTRACT;

    SIZE_T managedSize = 0;

    if (pinnedArg != NULL)
    {
        // Can pass null objects to interop, only check the size if the object is valid.
        managedSize = pinnedArg->GetSize();
    }

    if (target != NULL)
    {
        STRESS_LOG3(LF_STUBS, LL_INFO100, "Managed object %#X with size '%#X' pinned for interop to Method [%pM]\n", pinnedArg, managedSize, target);
    }
    else
    {
        STRESS_LOG2(LF_STUBS, LL_INFO100, "Managed object %#X pinned for interop with size '%#X'", pinnedArg, managedSize);
    }
}
FCIMPLEND

FCIMPL1(DWORD, StubHelpers::CalcVaListSize, VARARGS *varargs)
{
    FCALL_CONTRACT;

    return VARARGS::CalcVaListSize(varargs);
}
FCIMPLEND

FCIMPL2(void, StubHelpers::MulticastDebuggerTraceHelper, Object* element, INT32 count)
{
    FCALL_CONTRACT;
    FCUnique(0xa5);
}
FCIMPLEND

FCIMPL0(void*, StubHelpers::NextCallReturnAddress)
{
    FCALL_CONTRACT;
    UNREACHABLE_MSG("This is a JIT intrinsic!");
}
FCIMPLEND
