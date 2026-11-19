namespace Tradux.Knowledge;

/// <summary>Seção <c>Tradux:Knowledge</c> (ADR-0013 §8–§11).</summary>
public sealed class KnowledgeOptions
{
    public const string SectionName = "Tradux:Knowledge";

    /// <summary>Raiz do repositório/corpus (onde está <c>docs/rag/sources.json</c>); vazio = busca indisponível, salvo índice pronto.</summary>
    public string? CorpusRoot { get; set; }

    /// <summary>Índice persistido: carregado ao iniciar e regravado após a indexação incremental.</summary>
    public string? IndexPath { get; set; }
}

/// <summary>
/// Índice de conhecimento do processo. Só apoio — o caminho de geração nunca
/// o consulta (ADR-0013 §7). Indexação incremental no primeiro uso; falha na
/// indexação NÃO fica em cache (a próxima busca tenta de novo — revisão da
/// Fase 10, A6). Reindexar = reiniciar a instância (runbook RAG_REINDEXACAO).
/// </summary>
/// <param name="options">Corpus e índice persistido.</param>
/// <param name="operators">Catálogo de operadores do motor, descrito pelo host (o RAG não referencia o motor).</param>
/// <param name="operatorsVersion">Versão do catálogo (metadado da fonte).</param>
/// <param name="onIndexFailure">Aviso de falha de indexação (o host registra só o tipo do erro).</param>
public sealed class KnowledgeService(
    KnowledgeOptions options, Func<IEnumerable<OperatorDoc>> operators, string operatorsVersion, Action<Exception> onIndexFailure)
{
    private readonly object _gate = new();
    private Bm25KnowledgeIndex? _index;
    private volatile bool _built;

    public bool Available => Index() is not null;

    public IReadOnlyList<SearchHit> Search(string query, AccessScope scope, int limit) =>
        Index()?.Search(query, scope, limit) ?? [];

    private Bm25KnowledgeIndex? Index()
    {
        if (_built)
        {
            return _index;
        }

        lock (_gate)
        {
            if (!_built)
            {
                try
                {
                    _index = Build();
                    _built = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                {
                    onIndexFailure(ex);
                    return null;
                }
            }

            return _index;
        }
    }

    private Bm25KnowledgeIndex? Build()
    {
        var index = !string.IsNullOrWhiteSpace(options.IndexPath) ? Bm25KnowledgeIndex.Load(options.IndexPath) : new Bm25KnowledgeIndex();
        if (!string.IsNullOrWhiteSpace(options.CorpusRoot))
        {
            KnowledgeIndexer.Index(index, KnowledgeCorpus.Global(options.CorpusRoot, [KnowledgeCorpus.OperatorCatalog(operators(), operatorsVersion)]));
            if (!string.IsNullOrWhiteSpace(options.IndexPath))
            {
                index.Save(options.IndexPath);
            }
        }

        return index.ChunkCount > 0 ? index : null;
    }
}
