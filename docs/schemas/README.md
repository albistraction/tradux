# Schemas

XSDs de referência e fixtures representativas usadas pelo motor e por QA.
Todas são **sintéticas e ilustrativas** — nenhuma representa um layout
oficial de prefeitura/fornecedor real (ver aviso no topo de cada arquivo).

- `exemplo-simples.xsd` (+ `.ANALISE.md`) — DPS minimalista, só escalares em
  um único `xs:sequence`. Fixture-alvo da Fase 2 (vertical slice do motor).
- `exemplo-composto.xsd` + `exemplo-composto-tipos.xsd` (+ `.ANALISE.md`) —
  `xs:include` (mesmo namespace), `xs:choice` real, coleção repetível via
  tipo nomeado incluído. Fixture-alvo da Fase 3 (importação completa; a
  *geração* de XML para choice/coleções é escopo da Fase 4).
- `exemplo-composto-pacote.zip` — os dois arquivos acima empacotados em ZIP,
  fixture-alvo de `XsdSchemaImporter.ImportPackage` (Fase 3).
- `exemplo-import-ns-a.xsd` + `exemplo-import-ns-b.xsd` — par minimalista
  exercitando `xs:import` entre namespaces diferentes (complementa o par
  composto, que só usa `xs:include`).
- `origem-exemplo.xsd` + `origem-exemplo.xml` — **contrato de origem**
  ilustrativo (Fase 12, ADR-0015): XML exportado por um ERP, com namespace,
  atributos, conteúdo simples com atributo, `xs:int`/decimal, elemento
  nulável e coleção. Mapeado para `exemplo-simples.xsd` nos testes e no e2e;
  use-os para experimentar o fluxo XML → XML no editor.

Mantido pelo agente `domain-xsd`.
