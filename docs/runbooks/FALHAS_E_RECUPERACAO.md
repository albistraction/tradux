# Falhas e recuperação

Como a plataforma se comporta em cada falha e o que fazer (Fase 11,
ADR-0014 §4). Números medidos em `docs/operations/PERFORMANCE.md`.

## Sinais de saúde

- `GET /health/live` → `Healthy` enquanto o processo responde. Use para
  reiniciar instância travada.
- `GET /health/ready` → `Healthy` com MongoDB alcançável; **`Degraded`
  (HTTP 200)** com MongoDB fora — a instância continua servindo gerações
  de templates aquecidos, então **não** a tire do balanceador por isso.
  Alerta de monitoramento em `Degraded`, não remoção.
- Métricas (OTLP): `tradux.generation.count` por resultado,
  `tradux.generation.duration` (percentis), `tradux.cache.lookups` (hit/miss).
  Faltas de cache persistentes fora de deploy/publicação = instância
  reiniciando ou versões fixadas muito variadas.

## MongoDB fora do ar

O que continua funcionando:
- Geração de templates **já aquecidos** na instância (nenhuma leitura no
  caminho quente).
- Credenciais já validadas: seguem aceitas por até
  `Tradux:Security:CredentialStaleGrace` (padrão 10 min) além do cache (30 s);
  a instância tenta o banco no máximo uma vez por validade (30 s) por
  credencial — só esse pedido espera o `ServerSelectionTimeout`.

O que falha (rápido, com `503` e `Retry-After: 5`):
- Primeira geração de um template/versão ainda não aquecido na instância
  (em ~`Tradux:MongoDb:ServerSelectionTimeout`, padrão 5 s).
- Credencial nunca vista pela instância; publicação, rollback, pacotes,
  auditoria, captura diagnóstica.

Procedimento:
1. Confirme em `/health/ready` (`Degraded`) e no log ("Falha na
   invalidação do cache de snapshots").
2. **Não reinicie as instâncias** enquanto o banco estiver fora: reiniciar
   esvazia o cache e transforma todo template em chave fria (503).
3. Suspenda publicações/rollbacks (vão falhar) até o banco voltar.
4. Se a queda passar de `CredentialStaleGrace`, as credenciais em cache
   expiram e tudo passa a `503`: priorize o MongoDB, ou aumente a carência
   **antes** de uma manutenção programada (troca de nó, upgrade).
5. Quando o banco volta: nada a fazer — o Change Stream reconecta
   sozinho (nova tentativa a cada `Tradux:Cache:ChangeFeedRetryDelay`) e
   reconcilia com um poll; credenciais são revalidadas no próximo pedido.

Segurança: numa queda **total** nenhuma revogação é possível (revogar grava
no banco). Numa **partição** — só algumas instâncias sem acesso ao banco —
uma revogação feita por uma instância saudável vale nas isoladas só depois
de validade + `CredentialStaleGrace` (até ~10,5 min). Revogação urgente
com instâncias isoladas: reinicie **as isoladas** (perdem o cache e passam a
recusar credenciais que não conseguem validar) ou reduza a carência.

## Change Stream interrompido (banco no ar)

- Sintoma: instância serve versão antiga depois de publicar/rollback.
- O poll de segurança (`Tradux:Cache:PollInterval`, 30 s) corrige sozinho;
  para forçar, reinicie a instância. Detalhes em INCIDENTES.md.

## Instância reiniciada ou nova (deploy, escala)

- Sobe com cache vazio: a primeira geração de cada versão compila o
  snapshot (milissegundos a segundos, conforme o XSD) e lê o banco uma vez;
  requisições concorrentes para a mesma chave compartilham essa única
  leitura e compilação.
- Deploy sem parada: suba as novas instâncias, espere `/health/ready`
  `Healthy`, envie uma geração de aquecimento por template crítico (opcional)
  e só então retire as antigas.

## Sobrecarga (CPU alta, latência subindo)

1. Confira `tradux.generation.duration` p99 e a taxa. Uma instância sustenta
   muito além de 2.000/min com o template de exemplo — sobrecarga indica
   template pesado, muitos pedidos com entrada grande ou log verboso.
2. Adicione instâncias (sem estado; o balanceador distribui livremente).
3. Reduza o log de console para Warning e use OTLP.
4. Limitação de taxa por cliente fica no gateway (não na API).

## Pedidos recusados por limite

- `413`: corpo acima de `Tradux:Generation:MaxRequestBytes` (1 MiB).
- `400` "profundidade/nós/caracteres": entrada acima de
  `Tradux:Generation:Limits` (32 níveis, 10.000 nós, 10.000 caracteres por
  texto). Ajuste o integrador; aumente o limite só com evidência (cada
  aumento amplia o pior caso de CPU e memória por pedido).

## Memória crescendo

- O cache guarda cada versão usada (ativa + fixadas + fallback). Muitas
  versões antigas fixadas por integradores fazem o cache crescer: peça que
  usem a versão ativa ou limite as versões em uso; reiniciar libera.
- Captura diagnóstica e auditoria não ficam em memória.

## Recuperação de desastre

- Backup completo = backup do MongoDB (snapshots, camadas, vínculos,
  auditoria, credenciais). Restaure o banco e suba as instâncias — o cache
  se reconstrói sob demanda.
- Backup lógico por template = pacote `.traduxpkg` (PROMOCAO_POR_PACOTE.md);
  ele restaura o template e as camadas, não auditoria nem credenciais.
- Rollback de versão (sem desastre): ROLLBACK_E_ATUALIZACOES.md.
