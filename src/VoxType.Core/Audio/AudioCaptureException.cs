namespace VoxType.Core.Audio;

public sealed class AudioCaptureException(string message, Exception? innerException = null) : Exception(message, innerException);
