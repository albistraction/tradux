# Requisitos

Fonte normativa completa e única: `docs/requirements/PROMPT_MESTRE_CLAUDE.md`.
Este documento é um índice de navegação para essa fonte — em caso de
divergência, o requisito de origem prevalece e este índice deve ser corrigido.

## Missão (seção 1)

Tradux: plataforma multiempresa de tradução de XML (origem → destino) baseada em XSD,
mapeamentos e regras configuráveis. Núcleo em C#, disponível como SDK
instalável e como API HTTP independente (ASP.NET Core), ambos sobre o mesmo
motor.

## Limites obrigatórios do núcleo (seção 2)

O núcleo só gera e valida XML. Não implementa: chamadas REST/SOAP a
prefeituras, autenticação em fornecedores, orquestração de emissão, polling,
consulta de lote, assinatura digital, criptografia, ou armazenamento de
certificados/chaves. Essas responsabilidades são de conectores externos. O
fallback interno é local (gera e valida), não descobre o que um endpoint
remoto aceita.

## Mapa de requisitos por área

| Área | Seção do requisito de origem | Onde vive o detalhe neste repositório |
|---|---|---|
| Regra de execução do Claude | 3 | `CLAUDE.md` |
| Estrutura de IA (agentes/skills/docs) | 4 | `.claude/`, esta árvore `docs/` |
| Agentes especialistas | 5 | `.claude/agents/` |
| Continuidade entre sessões | 6 | `docs/PROJECT_STATE.md`, `handoffs/` |
| Arquitetura do produto | 7–8 | `docs/architecture/` (a partir da Fase 1) |
| Modelo de domínio mínimo | 9 | `docs/domain/` (a partir da Fase 1) |
| Contratos de origem | 10 | `docs/domain/`, `docs/api/` |
| Importação segura de XSD | 11 | `.claude/skills/xsd-contract-analysis/`, `.claude/rules/security.md` |
| Editor visual | 12 | `docs/requirements/...#12`, implementação na Fase 6 |
| Motor de regras | 13–14 | `.claude/skills/rule-operator-design/`, implementação na Fase 4 |
| Geração do XML | 15 | `.claude/rules/dotnet.md`, implementação na Fase 2+ |
| Camadas, publicação e atualização | 16 | `.claude/skills/template-migration/`, implementação na Fase 7 |
| Versionamento e fallback | 17 | implementação na Fase 2+/5 |
| Modelo de erros | 18 | implementação na Fase 2+ |
| Testes obrigatórios | 19 | `.claude/rules/testing.md`, `.claude/skills/test-fixture-generation/` |
| Segurança e autorização | 20 | `.claude/rules/security.md`, implementação na Fase 9 |
| Persistência e cache | 21 | implementação na Fase 5 |
| Pacote portátil | 22 | implementação na Fase 10 |
| Observabilidade e diagnóstico | 23 | implementação na Fase 9 |
| RAG e documentação viva | 24 | `docs/rag/`, implementação na Fase 10 |
| Performance | 25 | `.claude/skills/performance-review/`, implementação na Fase 11 |
| API e SDK | 26 | `.claude/agents/api-sdk.md`, `docs/api/`, implementação na Fase 5 |
| Fases de implementação | 27 | `docs/ROADMAP.md` |
| Critérios globais de aceite | 28 | `docs/ROADMAP.md`, checklist final na Fase 11 |

## Critérios globais de aceite (seção 28, resumo)

- SDK e API usam o mesmo motor.
- XSD único e pacotes relacionados são importados com segurança.
- XML é construído a partir do schema, sem concatenação de strings.
- Obrigatórios sem mapeamento são detectados antes da publicação.
- Regras são tipadas e sem código arbitrário.
- Listas aninhadas funcionam.
- Camadas são rastreáveis.
- Atualização falha não substitui versão ativa.
- Todo XML liberado passa pelo XSD correto.
- Fallback informa erros por versão.
- Publicações são imutáveis e reversíveis.
- Testes estruturais e funcionais são obrigatórios.
- Runtime aquecido não depende de MongoDB por geração.
- Dados fiscais não vazam em logs.
- O núcleo não depende de REST, SOAP, certificados ou particularidades de
  fornecedor.
- A documentação e o handoff permitem continuidade por outro agente.
