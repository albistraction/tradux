# Domínio

Modelo de domínio da plataforma, cobrindo as entidades da seção 9 do
requisito de origem (`docs/requirements/PROMPT_MESTRE_CLAUDE.md`).

- **`DOMAIN_MODEL.md`** — modelo completo: todas as 23 entidades (atributos,
  identidade estável/versionada, relações), diagramas Mermaid, catálogo
  aberto de Operação, composição de Contrato de origem por módulos
  versionados/namespaced, cadeia de resolução
  template-base → produto → cliente → snapshot, e a distinção obrigatória
  entre "opcional" (cardinalidade) e "aceita nulo" (nulabilidade).

Fora de escopo deste diretório: estrutura interna de XSD (fixtures/análise
de schema concretos vão em `docs/schemas/`), código C# (Fase 2+), catálogo
técnico completo de operadores do motor de regras (Fase 4 — aqui só se
modela `Operator` como entidade de catálogo, não sua implementação).

Populado na Fase 1 pelos agentes `architect` e `domain-xsd`.
