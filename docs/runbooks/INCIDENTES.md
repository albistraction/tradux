# Incidentes

## API serve versão antiga depois de publicar ou rollback

- Causa provável: Change Stream interrompido. O poll de segurança
  (`Tradux:Cache:PollInterval`, padrão 30 s) corrige sozinho.
- Verifique no log o aviso "Falha na invalidação do cache de snapshots".
- Para forçar: reinicie a instância (o cache é só memória).

## MongoDB indisponível

- Gerações com cache aquecido continuam funcionando (o caminho quente não
  consulta o banco); publicação, rollback, auditoria, credencial nova e
  primeira geração de um template frio respondem `503` com `Retry-After`
  (em ~5 s, `Tradux:MongoDb:ServerSelectionTimeout`).
- Credenciais já autenticadas seguem válidas por até
  `Tradux:Security:CredentialStaleGrace` (10 min) com o banco fora.
- `/health/ready` fica `Degraded` (200) — não tire a instância do
  balanceador. Procedimento completo: FALHAS_E_RECUPERACAO.md.

## Geração falha com `XSD_VALIDATION_TIMEOUT`

- Um pattern do XSD de destino ficou caro demais para aquele valor (regex
  com retrocesso). A geração foi recusada de propósito — nenhuma thread
  presa.
- Simplifique o pattern (evite repetição aninhada `(a+)+` ou alternativas
  sobrepostas `(a|aa)+`). Patterns claramente perigosos já são recusados na
  importação do XSD.

## Publicação recusada

- `FUNCTIONAL_SCENARIO_REQUIRED`: falta cenário com XML esperado ou asserções.
- `STRUCTURAL_TEST_FAILED`: a suíte automática achou entrada válida que não
  gera XML válido (ou inválida aceita) — veja o relatório (caso, XPath).
- `SYNTHETIC_VALUE_UNAVAILABLE`: o gerador não consegue valor para um campo
  obrigatório (pattern fora do subconjunto) — inclua o campo num cenário.
- `LAYER_ACCESS_DENIED`: camada de outra organização não compartilhada (ou
  inexistente).

## Credencial não funciona

- `401`: segredo errado, revogado ou ausente. `403`: falta a permissão
  (veja o papel). `404` numa rota de outra organização é esperado.
