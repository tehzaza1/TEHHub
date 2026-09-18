using TEHhub.OffsetDoctor.Tests;

Console.WriteLine("=================================================");
Console.WriteLine("        TEHhub.OffsetDoctor Test Suite           ");
Console.WriteLine("=================================================");

int assertions = 0;
void Check(bool condition, string message)
{
    if (!condition)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FAIL] {message}");
        Console.ResetColor();
        throw new Exception($"Test assertion failed: {message}");
    }
    assertions++;
}

OffsetDoctorTests.RunAll(Check);

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"[PASS] {assertions} assertions verified. All OffsetDoctor tests passed.");
Console.ResetColor();
return 0;
