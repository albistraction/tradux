using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Tradux.Application.Ports;
using Tradux.Contracts;

namespace Tradux.Application.Diagnostics;

/// <summary>Janela de captura diagnóstica ligada para uma chave de template.</summary>
public sealed record CaptureWindow(TemplateKey Key, DateTimeOffset UntilUtc, string EnabledBy);

/// <summary>
/// Registro capturado — já mascarado. O armazenamento o criptografa em
/// repouso e o apaga sozinho em <see cref="ExpiresAtUtc"/> (seção 23).
/// </summary>
public sealed record DiagnosticRecord(
    string Id,
    TemplateKey Key,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? CorrelationId,
    string? VersionUsed,
    bool Success,
    string ErrorCodes,
    string MaskedInputJson,
    string? MaskedXml);

/// <summary>
/// Porta de armazenamento da captura diagnóstica. A implementação
/// criptografa os registros em repouso e usa expiração automática.
/// </summary>
public interface IDiagnosticCaptureStore
{
    /// <summary>Falso = armazenamento sem chave de criptografia: a captura não pode ser ligada.</summary>
    bool CanProtect { get; }

    Task SetWindowAsync(CaptureWindow window, CancellationToken cancellationToken = default);

    Task RemoveWindowAsync(TemplateKey key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaptureWindow>> ListActiveWindowsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    Task AddRecordAsync(DiagnosticRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiagnosticRecord>> ListRecordsAsync(TemplateKey key, CancellationToken cancellationToken = default);

    Task<long> DeleteRecordsAsync(TemplateKey key, CancellationToken cancellationToken = default);
}

/// <summary>Seção <c>Tradux:Diagnostics</c>.</summary>
public sealed class DiagnosticCaptureOptions
{
    public const string SectionName = "Tradux:Diagnostics";

    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Retenção dos registros (máximo aceito: 72 h).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Nomes de campo/elemento cujo valor é mascarado (regex, sem diferenciar maiúsculas).</summary>
    public string SensitiveNamePattern { get; set; } =
        "cpf|cnpj|documento|inscri|e-?mail|telefone|fone|celular|nome|razao|endereco|logradouro|numeroEndereco|complemento|bairro|cep|^rg$|nit|pis|senha|token|chave"
        // Abreviações do leiaute nacional (DPS): logradouro, número, complemento, endereço, IM, NIF, CAEPF.
        + "|^xLgr$|^nro$|^xCpl$|^xBairro$|^end$|^endNac$|^endExt$|^IM$|^NIF$|^CAEPF$|^xNome$|^fone$";

    /// <summary>Limite de registros por janela (evita encher o banco com uma captura esquecida).</summary>
    public int MaxRecordsPerWindow { get; set; } = 500;

    public static readonly TimeSpan RetentionCeiling = TimeSpan.FromHours(72);
}

/// <summary>Mascara valores de campos com nome sensível (JSON de entrada e XML gerado).</summary>
public sealed class DiagnosticMasker(string sensitiveNamePattern)
{
    private readonly Regex _sensitive = new(sensitiveNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public string MaskJson(JsonElement data)
    {
        var node = JsonNode.Parse(data.GetRawText());
        return Mask(node, sensitive: false)?.ToJsonString() ?? "null";
    }

    public string? MaskXml(string? xml)
    {
        if (xml is null)
        {
            return null;
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return "***";
        }

        foreach (var element in document.Descendants())
        {
            // Nome sensível no próprio elemento ou em qualquer ancestral (ex.: tudo
            // dentro de <end>) — como no JSON (revisão da Fase 9, W4).
            if (!element.HasElements && element.AncestorsAndSelf().Any(e => _sensitive.IsMatch(e.Name.LocalName)))
            {
                element.Value = MaskText(element.Value);
            }

            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration && _sensitive.IsMatch(a.Name.LocalName)))
            {
                attribute.Value = MaskText(attribute.Value);
            }
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private JsonNode? Mask(JsonNode? node, bool sensitive)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    obj[name] = Mask(child?.DeepClone(), sensitive || _sensitive.IsMatch(name));
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = Mask(array[i]?.DeepClone(), sensitive);
                }

                return array;
            case JsonValue value when sensitive:
                return JsonValue.Create(MaskText(value.ToJsonString().Trim('"')));
            default:
                return node;
        }
    }

    // Mantém só os 2 últimos caracteres (ajuda a conferir sem expor o valor).
    private static string MaskText(string value) => value.Length <= 2 ? "***" : "***" + value[^2..];
}

/// <summary>
/// Captura diagnóstica temporária e opt-in (seção 23, ADR-0012 §8). O estado
/// "ligada" vive em memória por processo, renovado por <see cref="RefreshAsync"/>
/// — o caminho aquecido de geração nunca consulta o banco para decidir.
/// Ligar, desligar, ler e apagar são auditados.
/// </summary>
public sealed class DiagnosticCaptureService
{
    private readonly IDiagnosticCaptureStore _store;
    private readonly AuditTrail _audit;
    private readonly DiagnosticCaptureOptions _options;
    private readonly DiagnosticMasker _masker;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<TemplateKey, DateTimeOffset> _active = new();
    private readonly ConcurrentDictionary<TemplateKey, int> _recorded = new();

    public DiagnosticCaptureService(IDiagnosticCaptureStore store, IAuditSink audit, DiagnosticCaptureOptions? options = null, TimeProvider? time = null)
    {
        _store = store;
        _options = options ?? new DiagnosticCaptureOptions();
        if (_options.Retention <= TimeSpan.Zero || _options.Retention > DiagnosticCaptureOptions.RetentionCeiling)
        {
            throw new ArgumentException($"Retenção da captura diagnóstica deve ser entre 0 e {DiagnosticCaptureOptions.RetentionCeiling.TotalHours} h.");
        }

        _masker = new DiagnosticMasker(_options.SensitiveNamePattern);
        _time = time ?? TimeProvider.System;
        _audit = new AuditTrail(audit, _time);
    }

    public bool Available => _store.CanProtect;

    /// <exception cref="InvalidOperationException">Sem chave de criptografia configurada.</exception>
    /// <exception cref="ArgumentException">Duração fora de (0, máximo].</exception>
    public async Task<CaptureWindow> EnableAsync(TemplateKey key, TimeSpan? duration, string actor, CancellationToken cancellationToken = default)
    {
        if (!_store.CanProtect)
        {
            throw new InvalidOperationException("Captura diagnóstica indisponível: configure a chave de criptografia (Tradux:Diagnostics:EncryptionKey).");
        }

        var length = duration ?? _options.DefaultDuration;
        if (length <= TimeSpan.Zero || length > _options.MaxDuration)
        {
            throw new ArgumentException($"Duração da captura deve ser entre 1 minuto e {_options.MaxDuration.TotalMinutes} minutos.");
        }

        var window = new CaptureWindow(key, _time.GetUtcNow() + length, actor);
        await _store.SetWindowAsync(window, cancellationToken).ConfigureAwait(false);
        _active[key] = window.UntilUtc;
        _recorded[key] = 0;
        await AuditAsync(key, actor, "diagnostic.capture.enable", new() { ["until"] = window.UntilUtc.ToString("O") }, cancellationToken).ConfigureAwait(false);
        return window;
    }

    public async Task DisableAsync(TemplateKey key, string actor, CancellationToken cancellationToken = default)
    {
        await _store.RemoveWindowAsync(key, cancellationToken).ConfigureAwait(false);
        _active.TryRemove(key, out _);
        await AuditAsync(key, actor, "diagnostic.capture.disable", [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decisão em memória: a captura está ligada para esta chave agora?</summary>
    public bool ShouldCapture(TemplateKey key) =>
        _active.TryGetValue(key, out var until) && until > _time.GetUtcNow()
        && _recorded.GetValueOrDefault(key) < _options.MaxRecordsPerWindow;

    /// <summary>Grava a geração mascarada. Falha aqui nunca derruba a geração (o chamador ignora).</summary>
    public async Task CaptureAsync(TemplateKey key, JsonElement input, GenerationResult result, CancellationToken cancellationToken = default)
    {
        if (!ShouldCapture(key))
        {
            return;
        }

        _recorded.AddOrUpdate(key, 1, (_, count) => count + 1);
        var now = _time.GetUtcNow();
        await _store.AddRecordAsync(new DiagnosticRecord(
            Guid.NewGuid().ToString("N"),
            key,
            now,
            now + _options.Retention,
            result.CorrelationId,
            result.VersionUsed,
            result.Success,
            Observability.TraduxTelemetry.ErrorCodes(result),
            _masker.MaskJson(input),
            _masker.MaskXml(result.Xml)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Leitura de dado sensível: sempre auditada.</summary>
    public async Task<IReadOnlyList<DiagnosticRecord>> ReadAsync(TemplateKey key, string actor, CancellationToken cancellationToken = default)
    {
        var records = await _store.ListRecordsAsync(key, cancellationToken).ConfigureAwait(false);
        await AuditAsync(key, actor, "diagnostic.read", new() { ["records"] = records.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
        return records;
    }

    public async Task<long> DeleteAsync(TemplateKey key, string actor, CancellationToken cancellationToken = default)
    {
        var deleted = await _store.DeleteRecordsAsync(key, cancellationToken).ConfigureAwait(false);
        await AuditAsync(key, actor, "diagnostic.delete", new() { ["records"] = deleted.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>Renova o estado em memória a partir do armazenamento (outras instâncias ligaram/desligaram).</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var windows = await _store.ListActiveWindowsAsync(now, cancellationToken).ConfigureAwait(false);
        var current = windows.ToDictionary(w => w.Key, w => w.UntilUtc);
        foreach (var key in _active.Keys.Where(k => !current.ContainsKey(k)).ToList())
        {
            _active.TryRemove(key, out _);
        }

        foreach (var (key, until) in current)
        {
            if (!_active.TryGetValue(key, out var known) || known != until)
            {
                _recorded[key] = 0; // janela nova (ou renovada) em outra instância
            }

            _active[key] = until;
        }
    }

    private Task AuditAsync(TemplateKey key, string actor, string action, Dictionary<string, string> details, CancellationToken cancellationToken) =>
        _audit.RecordAsync(key, actor, action, AuditTrail.Success, details, cancellationToken);
}
