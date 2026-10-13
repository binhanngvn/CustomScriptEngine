// Copy this file to BepInEx/scripts/shared/ExampleMessage.cs.
namespace CustomScriptEngine.Examples.Shared;

public static class ExampleMessage
{
    public static string Format(string message)
        => "[SharedScriptExample] " + message;
}
