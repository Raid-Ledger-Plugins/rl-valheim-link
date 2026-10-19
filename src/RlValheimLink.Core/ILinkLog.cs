namespace RlValheimLink.Core
{
    /// <summary>The plugin adapts this to BepInEx's logger (with token redaction); tests capture it.</summary>
    public interface ILinkLog
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }
}
