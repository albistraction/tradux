using Tradux.Engine.Operators;
using Xunit.Abstractions;

namespace Tradux.Knowledge.Tests;

/// <summary>Fase 10 (seção 24, ADR-0013): chunking, indexação incremental, acesso, persistência e avaliação.</summary>
public class KnowledgeTests(ITestOutputHelper output)
{
    private static string Root => RepositoryPaths.Root;

    internal static KnowledgeSource OperatorCatalog() => KnowledgeCorpus.OperatorCatalog(
        RuleOperatorMetadata.BuiltIn.Select(o => new OperatorDoc(o.Key, o.Value.Category, o.Value.Description,
            o.Value.Parameters.Select(p => new OperatorParameterDoc(p.Name, p.Kind.ToString(), p.Description)).ToList())),
        "engine-builtin");

    private static KnowledgeSource Md(string id, string content, string scope = KnowledgeSource.GlobalScope, AccessLevel level = AccessLevel.Internal) => new()
    {
        SourceId = id, SourceType = "runbook", Version = "1", Content = content, TenantScope = scope, AccessLevel = level,
    };

    // ------------------------------------------------------------------ chunking

    [Fact]
    public void Markdown_is_chunked_by_section_with_heading_path_and_size_limit()
    {
        var longSection = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Parágrafo {i} " + new string('x', 90)));
        var chunks = Chunker.Chunk(Md("r", $"# Título\nIntro\n## Rollback\nVolte a versão.\n### Detalhe\n{longSection}"));

        Assert.Contains(chunks, c => c.Metadata.Anchor == "Título > Rollback" && c.Text.Contains("Volte a versão."));
        var detail = chunks.Where(c => c.Metadata.Anchor == "Título > Rollback > Detalhe").ToList();
        Assert.True(detail.Count > 1);
        Assert.All(detail, c => Assert.True(c.Text.Length <= Chunker.MaxChunkChars + 400));
        Assert.All(chunks, c => Assert.Equal(("r", "1", "global"), (c.Metadata.SourceId, c.Metadata.Version, c.Metadata.TenantScope)));
        Assert.Equal(chunks.Select(c => c.Metadata.ContentHash), Chunker.Chunk(Md("r", $"# Título\nIntro\n## Rollback\nVolte a versão.\n### Detalhe\n{longSection}")).Select(c => c.Metadata.ContentHash));
    }

    [Fact]
    public void Glossary_xsd_and_operator_catalog_have_their_own_chunking()
    {
        var glossary = Chunker.Chunk(new KnowledgeSource { SourceId = "g", SourceType = "glossary", Version = "1", Content = File.ReadAllText(Path.Combine(Root, "docs", "GLOSSARY.md")) });
        Assert.Contains(glossary, c => c.Metadata.Anchor == "Camada de cliente");

        var xsd = Chunker.Chunk(new KnowledgeSource { SourceId = "x", SourceType = "xsd", Version = "1", Content = File.ReadAllText(Path.Combine(Root, "docs", "schemas", "exemplo-simples.xsd")) });
        Assert.Contains(xsd, c => c.Metadata.Anchor == "simpleType:Cnpj14Type" && c.Text.Contains(@"\d{14}"));

        var operators = Chunker.Chunk(OperatorCatalog());
        Assert.Equal(RuleOperatorMetadata.BuiltIn.Count, operators.Count);
        Assert.Contains(operators, c => c.Metadata.Anchor == "operador:formatDate");
    }

    // ------------------------------------------------------------------ pipeline incremental

    [Fact]
    public void Reindexing_is_incremental_by_hash_and_removes_obsolete_chunks()
    {
        var index = new Bm25KnowledgeIndex();
        var a = Md("a", "# A\n## Um\nprimeiro texto\n## Dois\nsegundo texto");
        var b = Md("b", "# B\nconteúdo b");

        var first = KnowledgeIndexer.Index(index, [a, b]);
        Assert.Equal(["a", "b"], first.AddedSources);

        var second = KnowledgeIndexer.Index(index, [a, b]);
        Assert.Equal(["a", "b"], second.UnchangedSources);
        Assert.Equal((0, 0), (second.ChunksWritten, second.ChunksRemoved));

        // Muda só uma seção de "a": uma seção regravada, a outra mantida.
        var third = KnowledgeIndexer.Index(index, [a with { Content = "# A\n## Um\nprimeiro texto\n## Dois\nsegundo texto alterado" }, b]);
        Assert.Equal(["a"], third.UpdatedSources);
        Assert.Equal((1, 1), (third.ChunksWritten, third.ChunksRemoved));
        Assert.DoesNotContain(index.Search("segundo texto alterado", new AccessScope(null, AccessLevel.Internal)), h => h.SourceId != "a");

        // "b" saiu do corpus: some do índice.
        var fourth = KnowledgeIndexer.Index(index, [a with { Content = "# A\n## Um\nprimeiro texto\n## Dois\nsegundo texto alterado" }]);
        Assert.Equal(["b"], fourth.RemovedSources);
        Assert.Empty(index.Search("conteúdo", new AccessScope(null, AccessLevel.Restricted)));
    }

    [Fact]
    public void Retrieval_respects_tenant_scope_and_access_level_and_always_cites_source_and_version()
    {
        var index = new Bm25KnowledgeIndex();
        KnowledgeIndexer.Index(index, [Md("global-doc", "# Rollback\nrollback de template global"), Md("restrito", "# Rollback\nrollback restrito interno", level: AccessLevel.Restricted)]);
        KnowledgeIndexer.Index(index, [Md("caso-a", "# Rollback\nrollback do cliente org-a", scope: "org-a")], "org-a");
        KnowledgeIndexer.Index(index, [Md("caso-b", "# Rollback\nrollback do cliente org-b", scope: "org-b")], "org-b");

        var forA = index.Search("rollback", new AccessScope("org-a", AccessLevel.Internal), 10);
        Assert.Equal(["caso-a", "global-doc"], forA.Select(h => h.SourceId).Order());
        Assert.DoesNotContain(index.Search("rollback org-b", new AccessScope("org-a", AccessLevel.Restricted), 10), h => h.SourceId == "caso-b");
        Assert.Contains(index.Search("rollback", new AccessScope("org-a", AccessLevel.Restricted), 10), h => h.SourceId == "restrito");
        Assert.All(forA, h => Assert.False(string.IsNullOrEmpty(h.Version) || string.IsNullOrEmpty(h.Anchor)));

        // Reindexar o escopo global não apaga fontes de organizações.
        KnowledgeIndexer.Index(index, [Md("global-doc", "# Rollback\nrollback de template global")]);
        Assert.Contains("caso-a", index.SourceIds);
        Assert.DoesNotContain("restrito", index.SourceIds);
        Assert.Throws<ArgumentException>(() => KnowledgeIndexer.Index(index, [Md("x", "y", scope: "org-a")]));
    }

    [Fact]
    public void Index_persists_and_reloads_with_the_same_results()
    {
        var index = new Bm25KnowledgeIndex();
        KnowledgeIndexer.Index(index, KnowledgeCorpus.Global(Root, [OperatorCatalog()]));
        var path = Path.Combine(Path.GetTempPath(), $"tradux-knowledge-{Guid.NewGuid():N}.json");
        try
        {
            index.Save(path);
            var reloaded = Bm25KnowledgeIndex.Load(path);
            var scope = new AccessScope("org-a", AccessLevel.Internal);

            Assert.Equal(index.Search("rollback versão anterior", scope), reloaded.Search("rollback versão anterior", scope));
            Assert.Equal(["adr", "operator-catalog"], new[] { "adr", "operator-catalog" }.Where(p => reloaded.SourceIds.Any(s => s == p || s.StartsWith(p + ":", StringComparison.Ordinal))));
            Assert.Empty(KnowledgeIndexer.Index(reloaded, KnowledgeCorpus.Global(Root, [OperatorCatalog()])).UpdatedSources);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Index_file_in_another_format_is_refused_on_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tradux-knowledge-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"format":"tradux-knowledge-index/0","sources":[],"chunks":[]}""");

            var ex = Assert.Throws<InvalidDataException>(() => Bm25KnowledgeIndex.Load(path));
            Assert.Contains(Bm25KnowledgeIndex.PersistedFormat, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------ avaliação (gate)

    [Fact]
    public void Evaluation_meets_the_recorded_thresholds()
    {
        var index = new Bm25KnowledgeIndex();
        KnowledgeIndexer.Index(index, KnowledgeCorpus.Global(Root, [OperatorCatalog()]));
        var questions = KnowledgeEvaluator.Load(Path.Combine(Root, "docs", "rag", "eval-set.json"));

        var result = KnowledgeEvaluator.Evaluate(index, questions, new AccessScope("org-a", AccessLevel.Internal), k: 5);

        var report = string.Join("\n", result.Details.Select(d => $"{(d.Covered ? "ok " : "NÃO")} p={d.Precision:0.00} {d.Question} → {string.Join(", ", d.Retrieved)}"));
        output.WriteLine($"precisão@5={result.PrecisionAtK:0.000} cobertura={result.Coverage:0.000}\n{report}");
        Assert.True(result.Coverage >= 0.9, $"cobertura {result.Coverage:0.00}\n{report}");
        Assert.True(result.PrecisionAtK >= 0.5, $"precisão@5 {result.PrecisionAtK:0.00}\n{report}");
    }
}
