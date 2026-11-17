__attribute__((export_name("factorial")))
int factorial(int n) {
    if (n <= 1) return 1;
    return n * factorial(n - 1);
}

__attribute__((export_name("fibonacci")))
int fibonacci(int n) {
    if (n <= 1) return n;
    return fibonacci(n - 1) + fibonacci(n - 2);
}

__attribute__((export_name("gcd")))
int gcd(int a, int b) {
    while (b != 0) {
        int t = b;
        b = a % b;
        a = t;
    }
    return a;
}
