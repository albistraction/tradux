namespace Tradux.Application.Snapshots;

/// <summary>Um snapshot não pôde ser importado/compilado neste processo (ex.: XSD inválido, operador customizado não registrado).</summary>
public sealed class SnapshotCompilationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
