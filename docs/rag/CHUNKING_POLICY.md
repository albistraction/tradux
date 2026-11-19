# Política de chunking (RAG)

Implementada em `src/Tradux.Knowledge/Chunker.cs` (Fase 10, ADR-0013 §9).
Determinística: o mesmo conteúdo produz os mesmos chunks e os mesmos hashes —
é o que permite reindexação incremental.

| `source_type` | Corte | Âncora |
|---|---|---|
| `requirement`, `adr`, `domain`, `architecture`, `operations`, `api`, `runbook` (Markdown) | por seção `#`/`##`/`###`; seção acima de ~1200 caracteres é dividida por parágrafos, com sobreposição de um parágrafo curto | caminho de títulos (`Título > Seção > Subseção`) |
| `glossary` | um chunk por termo (`- **Termo**: definição`, com continuação indentada) | o termo |
| `xsd` | uma declaração de topo por chunk (`element`, `simpleType`, `complexType`, `group`, `attributeGroup`), com documentação e restrições | `simpleType:Nome` etc. |
| `operator-catalog` | um operador por chunk (gerado do catálogo tipado do motor pelo host) | `operador:nome` |

Metadados de todo chunk: `source_id`, `source_type`, `version`,
`tenant_scope`, `provider`, `municipality`, `operation`, `schema_version`,
`language`, `content_hash`, `effective_date` (seção 24) + `access_level` e
`anchor`.

Reindexação: fonte com hash igual não é reprocessada; chunk com hash igual é
mantido; fonte removida do registro tem os chunks apagados (só dentro do
escopo indexado — `global` não apaga fontes de organizações).
