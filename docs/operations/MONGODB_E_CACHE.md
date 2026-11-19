# MongoDB e cache de snapshots — requisitos operacionais

Fase 5. Decisões de origem: ADR-0004 (persistência), ADR-0005 (cache e
invalidação), ADR-0008 (fachadas e conteúdo do snapshot).

## Pré-requisito: replica set

O MongoDB usado pela API (e por qualquer host do SDK que use o adaptador
MongoDB) **precisa ser um replica set** — mesmo que de um único nó:

- a publicação grava o novo snapshot ativo e rebaixa o anterior numa
  transação multi-documento;
- a invalidação de cache usa Change Streams.

Contra um `mongod` standalone, a publicação falha (transação indisponível)
e o feed de mudanças entra em loop de reconexão (registrado em log como
aviso), restando só o poll de segurança.

## Configuração da API (`appsettings.json`, seção `Tradux`)

| Chave | Padrão | Significado |
|---|---|---|
| `Tradux:MongoDb:ConnectionString` | — (obrigatório) | Conexão com o replica set. Ex.: `mongodb://host:27017/?replicaSet=rs0`. |
| `Tradux:MongoDb:DatabaseName` | `tradux-mapping` | Banco. |
| `Tradux:MongoDb:SnapshotCollectionName` | `compiledSnapshots` | Coleção de snapshots. |
| `Tradux:Cache:InvalidationEnabled` | `true` | Liga Change Streams + poll. Desligar só em teste. |
| `Tradux:Cache:PollInterval` | `00:00:30` | Intervalo do poll de segurança (lê só o ponteiro de versão ativa das chaves em cache). |
| `Tradux:Cache:ChangeFeedRetryDelay` | `00:00:05` | Espera antes de reconectar o Change Stream após falha. |

## Coleção `compiledSnapshots` e índices

Cada repositório cria os índices da sua coleção antes da primeira escrita da
instância (idempotente; um só mecanismo, `IndexGuard`):

| Índice | Campos | Propósito |
|---|---|---|
| `ux_key_version` (único) | organizationId, templateId, operation, snapshotVersion desc | Versão nunca se repete por chave. |
| `ux_key_active` (único, parcial `status = "active"`) | organizationId, templateId, operation | O banco recusa dois ativos para a mesma chave; também atende a consulta do ativo. |
| `ix_org_status` | organizationId, status | Consultas por organização. |
| `ix_content_hash` | contentHash | Localizar snapshot por conteúdo. |

Campos de fornecedor/município/base do ADR-0004 entram quando o modelo de
camadas (Fase 7) os introduzir no snapshot.

Um documento publicado nunca tem conteúdo alterado — a única escrita
posterior é `status: active → superseded`.

## Comportamento do cache

- Local a cada instância (sem Redis). Primeira geração de uma chave em uma
  instância: uma leitura do MongoDB + compilação; seguintes: zero acesso
  ao MongoDB (provado por `Phase5GateTests.Warm_cache_generation_sends_no_command_to_mongodb`).
- "Não encontrado" e falha de compilação não ficam em cache: cada pedido
  para uma chave inexistente faz uma leitura ao MongoDB. Aceitável
  enquanto a API só roda em rede confiável; revisar junto com
  autenticação/limites de taxa (Fase 9).
- O Change Stream não usa resume token: eventos durante uma reconexão são
  recuperados pelo poll feito logo após reconectar e pelo poll periódico.
- Publicação em qualquer instância/processo → Change Stream → cada
  instância recompila a versão nova e troca a entrada inteira; requisições
  em andamento terminam com a versão anterior.
- Se a nova versão não compilar nesta instância (ex.: operador customizado
  não registrado), a entrada anterior continua servindo e o erro vai para o
  log.
- Versão fixada (`version` no pedido) é imutável e fica em cache sem
  invalidação.

## Testes locais

Os testes de integração (`tests/Tradux.Api.IntegrationTests`) sobem
um `mongod` real via pacote `EphemeralMongo` (replica set de um nó, sem
Docker). Na primeira execução o pacote baixa os binários do MongoDB — exige
acesso à internet uma vez; depois usa o cache local.

## Coleções da Fase 7 (camadas, publicação, atualização — ADR-0010)

| Coleção | Conteúdo | Índices |
|---|---|---|
| `templateLayers` | versões imutáveis de camadas (base com XSD em binário; overrides em JSON canônico) | `ux_layer_version` (org, camada, versão desc) único |
| `templateBindings` | vínculo template → política, cenários funcionais, linhagem | `ux_binding_key` único; `ix_lineage` (propagação) |
| `templateUpdates` | propostas e tentativas de atualização (aplicadas, reprovadas, rejeitadas) | `ix_update_key` (chave + data) |
| `auditEvents` | trilha de auditoria sem payload fiscal | `ix_audit_org_time` |

`compiledSnapshots` ganhou `manifestJson` (camadas usadas e história de cada
regra). Rollback reativa um snapshot na mesma transação que rebaixa o ativo
— o Change Stream propaga para os caches como numa publicação. Os índices
das coleções novas seguem a mesma regra: criados antes da primeira escrita.
