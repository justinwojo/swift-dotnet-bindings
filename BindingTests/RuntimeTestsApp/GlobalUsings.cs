// Global using alias: generated bindings name the free-function class "Functions",
// so we alias it as "TestLibFunctions" for convenient access in test files.
global using TestLibFunctions = SwiftBindingsTestLib.Functions;

// The test library declares its own `Exception` error type, which would make a bare `Exception`
// ambiguous in every test file that imports the library namespace.
global using Exception = global::System.Exception;
