# ADR-0003: Versionamento imutável de contratos, schemas, templates, camadas e snapshots

- **Status**: aceito
- **Data**: 2026-09-28
- **Fase**: 1 — Descoberta técnica e arquitetura executável

## Contexto

O requisito de origem exige versões imutáveis de contratos, schemas,
templates, camadas e snapshots (seção 17); módulos de contrato de origem
nomeados e versionados, com exemplo explícito de notação `dps@1.x` (seção
10); resolução de camadas que "explique a origem de cada regra" e permita
rollback (seção 16); e índices de persistência por, entre outros, "versão"
"status" "base" e "hash" (seção 21). É preciso definir um esquema único de
identificação de versão, um mecanismo concreto de garantia de imutabilidade,
e como um snapshot referencia exatamente as versões que o compõem.

## Decisão

1. **Identificação**: todo artefato versionável (módulo de contrato de
   origem, conjunto de schema/XSD de destino, camada de template — base,
   produto ou cliente) é identificado por `nome@MAJOR.MINOR` (semver truncado
   em dois componentes, sem PATCH em nível de contrato — ex.: `dps@1.2`)
   mais um `contentHash` (SHA-256 do conteúdo canonicalizado). MAJOR
   incrementa em mudança que quebra compatibilidade (campo obrigatório
   removido/renomeado, tipo alterado, cardinalidade restringida, valor de
   enumeração removido). MINOR incrementa em mudança compatível (novo campo
   opcional, novo valor de enumeração, novo operador disponível). Uma
   dependência entre módulos pode ser expressa como faixa (`dps@1.x`),
   aceitando qualquer MINOR dentro do MAJOR.
2. **Imutabilidade**: cada versão publicada é um documento com campo
   `status` (`draft` → `published` → `archived`). A partir do momento em que
   `status = published`, nenhum campo de conteúdo pode ser alterado — os
   repositórios de infraestrutura (`Tradux.Infrastructure.MongoDb`)
   recusam updates de conteúdo em documentos publicados; a única mutação
   permitida em um documento publicado é a transição de `status` (ex.:
   `published` → `archived`). Qualquer correção de conteúdo cria uma nova
   versão (nova MINOR ou MAJOR, conforme o tipo de mudança), com seu próprio
   `contentHash`.
3. **Rastreabilidade do snapshot**: todo snapshot compilado publicado grava,
   de forma imutável, um manifesto explícito listando a versão exata
   (`nome@MAJOR.MINOR` + `contentHash`) de cada módulo de contrato de origem
   usado, do conjunto de schema/XSD de destino, e de cada camada (base,
   produto, cliente) que contribuiu para o resultado — permitindo reproduzir
   exatamente a resolução e servindo de base para o rollback (aponta o
   ponteiro de versão ativa de volta a um manifesto anterior).

## Alternativas consideradas

- **Versão única incremental global (v1, v2, v3…) sem semântica de
  breaking/non-breaking**: mais simples, mas não comunica ao consumidor nem
  à política de atualização automática (seção 16) se avançar de versão é
  seguro — rejeitada.
- **Versionamento apenas por timestamp/hash, sem número semântico**: garante
  imutabilidade e unicidade, mas não permite expressar dependência de faixa
  (`dps@1.x`, seção 10) nem comunicar compatibilidade de forma legível —
  rejeitada.
- **Ponteiro mutável "latest" atualizado in-place quando há correção**:
  rejeitada por violar diretamente "mantenha versões imutáveis" (seção 17),
  quebrar auditoria (seção 20) e impedir rollback determinístico (seção 16).

## Consequências

- Consumidores de módulo podem depender de uma faixa (`dps@1.x`) e receber
  correções MINOR automaticamente, sem risco de quebra por MAJOR não
  solicitado.
- Toda entidade versionável do modelo de domínio (`docs/domain/`) precisa de
  um campo `status` (`draft`/`published`/`archived`) e não pode expor uma
  operação de "editar conteúdo publicado" — isso é uma regra de aplicação, e
  deve ser reforçada por teste, não apenas por convenção de código.
- É preciso definir uma forma determinística de canonicalização de conteúdo
  antes de calcular `contentHash` (ordem de campos, encoding) — detalhamento
  cabe aos agentes `domain-xsd` e `dotnet-engine` antes das Fases 2–3.
- Rollback (seções 16 e 28) se torna uma operação barata: apontar o ponteiro
  de versão ativa de volta a um manifesto de snapshot já materializado, sem
  reprocessar a resolução de camadas.
- Custo aceito conscientemente: mais metadados por versão (nome, major,
  minor, hash, timestamp, autor, status) do que um esquema ingênuo de
  versão única — tratado como parte do custo necessário de auditabilidade
  fiscal, não como dívida técnica.
