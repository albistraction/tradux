# ADR-0004: Persistência via MongoDB por adaptadores de infraestrutura

- **Status**: aceito
- **Data**: 2026-09-28
- **Fase**: 1 — Descoberta técnica e arquitetura executável

## Contexto

O requisito de origem fixa MongoDB como armazenamento (seção 7), acessado
"somente por interfaces de repositório" (seção 21), com índices sugeridos
por organização, fornecedor, município, operação, versão, status, template,
base e hash (seção 21), e exige que o caminho normal de geração com cache
aquecido não consulte o MongoDB (seções 21 e 25). `CLAUDE.md` reforça que
domínio e motor (`Tradux.Domain`, `Tradux.Engine`) nunca dependem
de MongoDB. É preciso definir o formato de documento do snapshot compilado —
de forma que, uma vez aquecido em cache, ele nunca precise voltar ao Mongo —
e as coleções/índices principais.

## Decisão

- **Acesso exclusivo por porta/adaptador**: interfaces de repositório são
  definidas em `Tradux.Application` e implementadas em
  `Tradux.Infrastructure.MongoDb`. Nenhum tipo de `MongoDB.Driver` é
  referenciado por `Tradux.Domain` ou `Tradux.Engine`.
  (*Resolvido por ADR-0007*: a localização das portas — `Application`, não
  `Domain` — estava em aberto quando este ADR foi escrito; ADR-0007 fecha
  essa questão de forma definitiva.)
- **Snapshot compilado autocontido (denormalizado)**: a coleção
  `compiledSnapshots` armazena, por documento, a chave lógica completa
  (`organizationId`, `templateId`, `operation`, `providerId`,
  `municipalityId`, `snapshotVersion`, `contentHash`, `status`), o manifesto
  de versões de todas as camadas e contratos envolvidos (ADR-0003), e a
  árvore de mapeamentos já resolvida (base → produto → cliente, com
  proveniência por regra, ver ADR-0006) pronta para ser compilada
  (ADR-0001). O documento é intencionalmente denormalizado para que uma
  única leitura no cache-miss seja suficiente — sem joins contra outras
  coleções no caminho de execução.
- **Coleções principais**:
  - `sourceContracts` — versões de módulo de contrato de origem.
  - `destinationSchemas` — versões de conjunto de schema/XSD de destino.
  - `templateLayers` — documentos de camada (base, produto ou cliente), cada
    um referenciando sua camada-pai quando aplicável.
  - `publications` — registro append-only de cada publicação (quem, quando,
    versão resultante, alvo de rollback).
  - `compiledSnapshots` — snapshot materializado descrito acima.
  - `auditEvents` — eventos de auditoria (seção 20).
- **Índices**: índices compostos cobrindo, no mínimo, `organizationId`,
  `providerId` (fornecedor), `municipalityId`, `operation`, `version`,
  `status`, `templateId`, `baseTemplateId` e `contentHash`, priorizando o
  padrão de consulta mais frequente — resolução do snapshot ativo por
  `(organizationId, templateId, operation, status = active)`.

## Alternativas consideradas

- **Acesso direto ao `MongoDB.Driver` a partir do motor ou domínio, "para
  simplificar"**: rejeitada por violar explicitamente a Clean Architecture
  exigida (`CLAUDE.md`, seção 8 do requisito de origem), acoplar regras
  fiscais a infraestrutura de banco e impedir testar o motor sem um Mongo
  disponível.
- **Um único documento monolítico por produto, contendo todas as camadas de
  todos os clientes**: rejeitada — cresceria sem limite, dificultaria
  indexação e violaria isolamento por organização/tenant (seção 20).
- **Normalizar totalmente e resolver as camadas (join) em tempo de leitura a
  cada cache-miss**: tecnicamente correta, mas mais lenta e mais complexa do
  que necessário — a materialização em `compiledSnapshots` no momento da
  publicação (seção 16, "publicação materializa snapshot completo") já
  resolve isso uma vez; leitura em runtime deve ser O(1) por chave, não um
  join sob demanda — rejeitada.

## Consequências

- A publicação (Fase 7) passa a ser o único momento caro de acesso a dados
  (resolve camadas, compila, grava o documento denormalizado); a leitura em
  runtime é uma busca simples por chave.
- Exige disciplina para nunca editar um `compiledSnapshot` publicado in-place
  (reforça ADR-0003) — qualquer mudança gera um novo documento e um novo
  ponteiro de versão ativa (ver ADR-0005).
- Detalhamento fino de índices (compostos exatos, TTL se houver para
  `auditEvents`, etc.) fica a cargo do agente `mongodb-performance` na
  implementação (Fase 5); os campos obrigatórios de indexação já ficam
  fixados aqui como contrato mínimo.
- Trocar de banco de dados no futuro exigiria apenas uma nova implementação
  dos mesmos ports, sem tocar domínio ou motor — isolamento de risco aceito
  como benefício direto desta decisão.
