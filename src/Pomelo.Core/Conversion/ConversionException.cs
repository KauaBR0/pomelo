namespace Pomelo.Core.Conversion;

/// <summary>A per-file failure with a message meant for the user.</summary>
public sealed class ConversionException(string message, Exception? inner = null) : Exception(message, inner);
