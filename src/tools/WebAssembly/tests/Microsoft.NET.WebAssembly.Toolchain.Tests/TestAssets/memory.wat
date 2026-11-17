(module
  (memory (export "memory") 1)
  (func (export "store_and_load") (param $addr i32) (param $val i32) (result i32)
    (i32.store (local.get $addr) (local.get $val))
    (i32.load (local.get $addr))
  )
)
