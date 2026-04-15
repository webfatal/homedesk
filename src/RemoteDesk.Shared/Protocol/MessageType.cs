namespace RemoteDesk.Shared.Protocol;

/// <summary>
/// JSON message type strings used in the WebSocket protocol.
/// </summary>
public static class MessageType
{
    public const string Hello = "hello";
    public const string Config = "config";
    public const string VideoFrame = "video_frame";
    public const string InputMouse = "input_mouse";
    public const string InputKey = "input_key";
    public const string ClipboardSync = "clipboard_sync";
    public const string FileOffer = "file_offer";
    public const string FileChunk = "file_chunk";
    public const string SessionEvent = "session_event";
    public const string ControlRequest = "control_request";
    public const string ControlResponse = "control_response";
    public const string ConfigSync = "config_sync";
}
