# ADR-0007: Arquitetura geral — Clean Architecture, DDD pragmático, monólito modular, runtime sem estado

- **Status**: aceito
- **Data**: 2026-09-28
- **Fase**: 1 — Descoberta técnica e arquitetura executável

## Contexto

O backlog da Fase 1 (`docs/ROADMAP.md`) previa um ADR de arquitetura geral
cobrindo Clean Architecture, DDD pragmático, monólito modular e runtime sem
estado (seção 7 do requisito de origem). Os ADRs 0001–0006, produzidos em
paralelo por este mesmo lote de trabalho, já assumem essas decisões como
premissa (por exemplo, ADR-0004 fala em "acesso exclusivo por porta/
adaptador" e ADR-0001 depende de domínio/motor isolados de infraestrutura),
mas nenhum documento formalizava a decisão em si. Este ADR fecha essa lacuna
e resolve explicitamente uma ambiguidade levantada durante a Fase 1: se
`Tradux.Application` conhece apenas portas/interfaces de
infraestrutura, ou também tipos concretos.

## Decisão

- **Clean Architecture com direção de dependência única**: `Tradux.
  Domain` e `Tradux.Engine` (núcleo) não dependem de nenhum outro
  projeto da solução além de `Tradux.Xsd`/`Domain` entre si.
  `Tradux.Application` depende do núcleo e define as portas
  (interfaces) que a infraestrutura implementa — **`Application` conhece
  apenas interfaces de repositório/telemetria, nunca um tipo concreto de
  `MongoDB.Driver` ou de um SDK de observabilidade**. Isso resolve a
  ambiguidade apontada em `docs/architecture/CONTAINERS.md`: as portas vivem
  em `Application` (não em `Domain`, para não forçar o domínio a modelar
  conceitos de persistência), e são implementadas por
  `Tradux.Infrastructure.MongoDb`/`.Observability`.
- **DDD pragmático, não acadêmico**: usa-se linguagem ubíqua (ver
  `docs/GLOSSARY.md`) e agregados com identidade clara (ver
  `docs/domain/DOMAIN_MODEL.md`), mas sem exigir Event Sourcing, CQRS pleno
  ou um barramento de domínio — nenhum desses padrões é introduzido sem
  necessidade concreta demonstrada em fase posterior.
- **Monólito modular**: todos os projetos da seção 8 vivem em uma única
  solução e são implantados como um único processo de API (mais o SDK
  embutido em processos consumidores). Módulos internos (`Domain`, `Engine`,
  `Xsd`, `Application`) são isolados por projeto/namespace, não por
  processo — não há microsserviços nem filas nesta arquitetura, e nenhum
  deve ser introduzido sem evidência de necessidade (seção 25).
- **Runtime sem estado**: nenhum processo de API guarda estado necessário
  para atender a próxima requisição fora do cache de snapshot compilado
  (ADR-0005), que é reconstruível a partir do MongoDB a qualquer momento.
  Isso é o que permite escala horizontal simples (múltiplas instâncias
  idênticas atrás de um load balancer, sem afinidade de sessão).

## Alternativas consideradas

- **Portas definidas em `Domain` em vez de `Application`**: rejeitada — o
  domínio fiscal (entidades, invariantes de layout) não deveria precisar
  conhecer o conceito de "repositório" ou "sink de telemetria"; isso é
  preocupação de orquestração de caso de uso, papel de `Application`.
- **Microsserviços desde o início** (ex.: serviço de importação separado do
  serviço de geração): rejeitada nesta fase — a meta de performance (seção
  25) é atingível num monólito modular com cache local, e dividir em
  serviços adicionaria latência de rede e complexidade operacional sem
  benefício demonstrado.
- **CQRS/Event Sourcing completo para templates e publicações**: rejeitada
  como decisão *de agora* — o modelo de snapshot imutável + append-only
  (ADR-0003, ADR-0004) já cobre auditabilidade e rollback sem o custo de um
  event store dedicado; pode ser revisitado se um requisito futuro exigir
  reconstrução de estado histórico ponto-a-ponto que o log de publicações
  não cubra.

## Consequências

- Todo código de `Application` que precisar de persistência ou observabilidade
  declara uma interface própria (ex.: `ITemplateRepository`,
  `IAuditEventSink`) — implementá-la é responsabilidade exclusiva dos
  projetos `Infrastructure.*`, nunca de `Application` referenciar
  `MongoDB.Driver` diretamente.
- Testes de arquitetura (`Tradux.ArchitectureTests`, seção 8/
  `.claude/rules/testing.md`) devem, a partir da Fase 2, verificar
  automaticamente que `Domain` e `Engine` não referenciam nenhum pacote de
  infraestrutura, ASP.NET Core ou o projeto `Web` — essa regra deixa de
  depender só de disciplina humana assim que houver código para testar.
- Qualquer proposta futura de microsserviço, fila ou padrão CQRS/Event
  Sourcing pleno deve vir acompanhada de evidência de gargalo medido (não
  de preferência de design) — ver `.claude/skills/performance-review/
  SKILL.md`.
