# Reindexação do RAG e avaliações

O RAG é apoio à engenharia e ao editor: a geração de XML nunca o consulta e
nada que ele responde vira regra sem publicação formal (seção 24).

## Adicionar uma fonte

1. Aprove a fonte e registre em `docs/rag/sources.json` (`sourceId`,
   `path` — aceita `*` no nome do arquivo —, `sourceType`, `version`,
   `accessLevel`, `effectiveDate`).
2. Tipos com chunking próprio: `glossary` (por termo), `xsd` (por
   declaração de topo), `operator-catalog` (por operador); o resto é
   Markdown por seção.
3. Suba a `version` sempre que a fonte mudar de forma relevante — ela vai
   em toda resposta.

## Reindexar

A busca da API (`GET /v1/knowledge/search?q=`) indexa o corpus de
`Tradux:Knowledge:CorpusRoot` no primeiro uso e reaproveita
`Tradux:Knowledge:IndexPath` se configurado. A indexação é incremental por
hash: fonte inalterada não é reprocessada, chunk inalterado é mantido,
fonte removida do registro tem os chunks apagados.

A indexação roda uma vez por instância, na primeira busca; para reindexar
depois de mudar o corpus, **reinicie a instância** (ou gere o índice e
aponte `IndexPath` para ele). Se a indexação falhar (ex.: fonte registrada
que não existe), a busca responde `503` e tenta de novo na próxima
chamada. A gravação do índice é atômica (temporário + troca).

## Avaliar

`docs/rag/eval-set.json` tem perguntas com as fontes esperadas. O teste
`KnowledgeTests.Evaluation_meets_the_recorded_thresholds` mede precisão@5 e
cobertura e falha abaixo dos limiares de `docs/rag/RETRIEVAL_EVALS.md`.
Ao adicionar fontes, acrescente perguntas que as exercitem.

## Acesso

Fontes globais (`tenant_scope: global`) aparecem para qualquer credencial com
`template.read`; fontes de organização só para ela. `accessLevel`
`restricted` exige também `audit.read`.
