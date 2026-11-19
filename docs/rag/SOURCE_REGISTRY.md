# Registro de fontes aprovadas (RAG)

A fonte de verdade é **`docs/rag/sources.json`** (lido pelo indexador; ADR-0013
§9). Este documento explica as regras; a lista está no JSON.

- Só fontes aprovadas e versionadas (seção 24): requisitos, ADRs e log de
  decisões, glossário, documentação de domínio e arquitetura, operações,
  XSDs e suas análises, referência da API e guia do consumidor, runbooks e
  o catálogo de operadores (gerado do motor pelo host, id `operator-catalog`).
- `path` aceita `*` no nome do arquivo: cada arquivo vira uma fonte
  `<sourceId>:<nome>` (ex.: `adr:ADR-0012-seguranca-autorizacao-observabilidade`).
- `accessLevel`: `Public`, `Internal` (padrão) ou `Restricted` (ex.: modelo
  de ameaças — só credenciais com `audit.read` recuperam).
- `version` vai em toda resposta: suba quando a fonte mudar de forma
  relevante.
- Fontes de organização (ex.: casos de teste publicados de um tenant) usam
  `tenant_scope` = organização e são indexadas no escopo dela; nunca
  aparecem para outra organização.
- Um registro para arquivo inexistente faz a indexação falhar — o registro
  não pode mentir sobre o que está indexado.
- **O RAG não participa da geração de XML** e resposta dele nunca vira regra
  executável sem publicação formal.
