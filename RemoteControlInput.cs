namespace LanChat;

public sealed record RemoteControlInput(string Kind, int X, int Y, int Button, int Delta, int VirtualKey, bool KeyDown);
