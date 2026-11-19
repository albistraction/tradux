# Avaliações de recuperação (RAG)

Conjunto: `docs/rag/eval-set.json` (pergunta + fontes esperadas; prefixo
`adr` casa com `adr:<arquivo>`). Executado pelo teste
`Tradux.Knowledge.Tests.KnowledgeTests.Evaluation_meets_the_recorded_thresholds`
sobre o corpus real (`sources.json` + catálogo de operadores), escopo de uma
organização com nível `Internal`, k = 5.

## Métricas

- **Precisão@5**: média, por pergunta, da fração dos 5 resultados cuja fonte
  está entre as esperadas.
- **Cobertura**: fração das perguntas com ao menos uma fonte esperada entre
  os 5 resultados.

## Limiares (o teste falha abaixo)

| Métrica | Limiar | Último resultado |
|---|---|---|
| Cobertura | 0,90 | **1,00** (19/19) — 2026-09-30, Fase 11 |
| Precisão@5 | 0,50 | **0,57** — 2026-09-30, Fase 11 (0,60 com as 16 perguntas da Fase 10) |

Implementação avaliada: índice lexical BM25 (ADR-0013 §8). Ao trocar o
armazenamento (ex.: vetores), rode a mesma avaliação e registre aqui os
números antes/depois.

## Como evoluir

Ao aprovar uma fonte nova, acrescente perguntas que dependam dela. Perguntas
com cobertura falha indicam fonte faltando, título de seção pouco
descritivo ou vocabulário diferente entre pergunta e documento.
