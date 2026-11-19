# Instalação e configuração

## Pré-requisitos

- .NET SDK fixado em `global.json` (10.0.400) para compilar; runtime ASP.NET
  Core 10 para executar a API.
- MongoDB em **replica set** (transações e Change Streams — ADR-0004/0005).
  Um nó basta em desenvolvimento (`mongod --replSet rs0` + `rs.initiate()`).
- Node.js 20+ só para rodar os testes do editor.

## Configuração obrigatória da API

| Chave | Obrigatória | Descrição |
|---|---|---|
| `Tradux:MongoDb:ConnectionString` | sim | URI do replica set (`?replicaSet=rs0`) |
| `Tradux:MongoDb:DatabaseName` | não | padrão `tradux-mapping` |
| `Tradux:Security:BootstrapCredentials` | sim na 1ª subida | credenciais iniciais: `OrganizationId`, `Name`, `SecretSha256` (hash, nunca o segredo), `Roles` |
| `Tradux:Cors:AllowedOrigins` | se usar o editor | origem do editor (ex.: `https://editor.exemplo`) |
| `Tradux:Diagnostics:EncryptionKey` | para captura diagnóstica | 32 bytes em Base64 (AES-256); sem ela, captura indisponível (503) |
| `Tradux:Diagnostics:Retention` | não | padrão 24 h, máximo 72 h |
| `Tradux:Generation:MaxCandidateVersions` | não | padrão 5 (fallback) |
| `Tradux:Generation:MaxRequestBytes` | não | corpo máximo de `POST …/xml`, padrão 1 MiB (413 acima) |
| `Tradux:Generation:Limits:MaxDepth` / `MaxNodes` / `MaxTextLength` | não | limites da entrada de geração, padrão 32 / 10.000 / 10.000 (400 acima) |
| `Tradux:MongoDb:ServerSelectionTimeout` | não | padrão 5 s; com o MongoDB fora, chave fria falha em até este tempo (503) |
| `Tradux:Security:CredentialStaleGrace` | não | padrão 10 min: credencial já validada segue aceita com o MongoDB fora |
| `Tradux:Knowledge:CorpusRoot` / `Tradux:Knowledge:IndexPath` | para busca de conhecimento | raiz do corpus ou índice pré-gerado (ver RAG_REINDEXACAO.md) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | não | liga a exportação OTLP de traces, métricas e logs |
| `TRADUX_REGEX_TIMEOUT_MS` | não | timeout global de regex (padrão 1000 ms) |

## Subida local

```text
dotnet run --project src/Tradux.Api --launch-profile http   (:5220)
dotnet run --project src/Tradux.Web --launch-profile http   (:5230)
```

Em Development a credencial `tradux_dev-admin-org-a` (admin da org-a) já vem
configurada — só para uso local.

## Checklist de produção

1. `ASPNETCORE_ENVIRONMENT` **não** é `Development` (a credencial de
   desenvolvimento é pública).
2. Credenciais iniciais só com hash; entregue o segredo por canal seguro.
3. Chave da captura diagnóstica gerada aleatoriamente e guardada em cofre.
4. CORS só com a origem real do editor.
5. MongoDB com autenticação, TLS e backup; índices são criados no startup.
6. Várias instâncias da API são seguras (sem estado; cache invalidado por
   Change Streams). Revogação de credencial leva até 30 s entre instâncias
   (até 30 s + `CredentialStaleGrace` numa instância sem acesso ao MongoDB).
7. Limitação de taxa e WAF ficam no gateway (não na API).
8. Host do SDK: chame `RegexTimeout.Configure()` no início do processo.
9. Balanceador: liveness em `/health/live`; `/health/ready` só para
   saber quando uma instância nova está pronta — `Degraded` (MongoDB fora)
   não é motivo para tirar a instância (FALHAS_E_RECUPERACAO.md).
10. Mínimo de 2 instâncias (disponibilidade; a vazão de uma basta — ver
    `docs/operations/PERFORMANCE.md`). Console em nível Warning, OTLP ligado.
