using RlValheimLink.Core;

namespace RlValheimLink.Contract.Tests;

internal sealed class TestLog : ILinkLog
{
    public readonly List<string> Lines = new();
    public void Debug(string message) => Lines.Add("D " + message);
    public void Info(string message) => Lines.Add("I " + message);
    public void Warn(string message) => Lines.Add("W " + message);
    public void Error(string message) => Lines.Add("E " + message);
    public bool Any(string fragment) => Lines.Any(l => l.Contains(fragment));
    public override string ToString() => string.Join("\n", Lines);
}
