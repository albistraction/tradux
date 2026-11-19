# Manifesto do índice (RAG)

O índice é gerado, não versionado no repositório:

- Implementação: `Bm25KnowledgeIndex` (`src/Tradux.Knowledge`), porta
  `IKnowledgeIndex` substituível (ADR-0013 §8).
- Persistência: JSON em `Tradux:Knowledge:IndexPath` (formato
  `tradux-knowledge-index/1`: fontes com hash e escopo + chunks com metadados).
- A API indexa `Tradux:Knowledge:CorpusRoot` no primeiro uso (incremental sobre
  o índice carregado) e regrava o arquivo, se configurado.
- O que está indexado = o que `sources.json` registra no commit implantado +
  o catálogo de operadores daquele build (versão = versão do assembly).

Para conferir o conteúdo, rode a busca (`GET /v1/knowledge/search?q=...`):
todo resultado traz `sourceId`, `version`, `anchor` e `effectiveDate`.
