namespace Pomelo.Core;

/// <summary>A per-file failure with a message meant for the user.</summary>
public sealed class ConversionException(string message, Exception? inner = null) : Exception(message, inner);
