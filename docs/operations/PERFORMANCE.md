# Performance e capacidade

Resultados medidos da Fase 11 (ADR-0014). Metas da seção 25: dimensionar
para 100 a 200 gerações por minuto e testar com margem de dez vezes o pico
(2.000/min); nenhuma leitura do MongoDB com cache aquecido; percentis;
benchmarks separados de compilação e execução.

## Como foi medido

- **O quê**: `POST /v1/organizations/{org}/templates/{t}/operations/{op}/xml`
  com credencial de runtime, template DPS de exemplo (`SimpleDps` das
  fixtures: XSD de exemplo, mapeamento com regras e validação XSD completa)
  e a entrada realista `DpsJson`, cache aquecido.
- **Onde**: API real (`Program` de produção) servida por Kestrel com
  sockets reais, MongoDB 8 real (replica set de um nó, processo separado),
  cliente HTTP no mesmo processo da API.
- **Modelo aberto** para as taxas-alvo (dispara na taxa, sem esperar a
  resposta anterior — fila aparece como latência) e **modelo fechado** (16
  clientes em laço) para o teto de uma instância.
- **Máquina**: Intel Core i5-7400 (4 núcleos, 3,0 GHz), 16 GB, Windows 11,
  .NET SDK 10.0.400, build Debug (a de produção é Release — mais rápida).
- **Reproduzir**: `TRADUX_LOAD_SECONDS=60 dotnet test tests/Tradux.Api.IntegrationTests --filter "FullyQualifiedName~Phase11Performance"`.
  Relatórios JSON em `artifacts/perf/`. A suíte padrão roda as mesmas
  taxas numa janela de 15 s.

## Resultados (janela de 60 s, 2026-09-30)

| Cenário | Pedidos | Taxa atingida | Erros | p50 | p95 | p99 | máx | CPU* | Working set (início→pico→fim) | Comandos em snapshots** | Acerto de cache |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 200/min (pico) | 200 | 201/min | 0 | 0,7 ms | 4,4 ms | 17,3 ms | 28,7 ms | 2,8% | 124→143→139 MB | 0 | 100% |
| 2.000/min (margem 10×) | 2.000 | 2.001/min | 0 | 0,6 ms | 1,9 ms | 4,3 ms | 47,8 ms | 2,0% | 161→165→161 MB | 0 | 100% |
| Saturação (16 clientes, 10 s) | 44.769 | ~268.000/min | 0 | 1,4 ms | 14,6 ms | 26,0 ms | 133,8 ms | 41,8% | 144→157→157 MB | 0 | 100% |

\* CPU do processo (API + gerador de carga) sobre os 4 núcleos — limite
superior do custo da API. \*\* Comandos na coleção de snapshots durante a
janela, contados pela instrumentação do driver (não por contador nosso),
com a invalidação desligada no teste. Em produção há leituras de fundo que
não dependem da taxa de pedidos: poll de ponteiros (30 s), revalidação de
credencial em uso (30 s), janelas de captura diagnóstica (15 s) e o Change
Stream (ADR-0014 §2).

Metas formais do ADR-0014 §2: **todas atingidas** — 0 erros; p99 ≤ 100 ms a
200/min e ≤ 250 ms a 2.000/min; zero leitura; 100% de acerto; working set
estável (sem crescimento acima de 64 MB); teto de uma instância muito acima
de 2.000/min. A saturação variou entre execuções de ~128.000 a ~268.000/min
(o log de console por geração em Development pesa); qualquer valor fica
duas ordens de grandeza acima da margem.

Alocação: ~50–62 KB por geração (inclui cliente HTTP e serialização da
resposta); coletas de gen2: 0–1 por minuto de carga.

## Benchmarks separados (sem HTTP)

| Etapa | n | p50 | p99 |
|---|---|---|---|
| Compilação do snapshot (XSD + mapeamento → modelo executável) | 30 | 3,3 ms | 12,9 ms |
| Execução do motor (gerar + validar XSD) | 5.000 | 0,08 ms | 1,4 ms |

A compilação só acontece na primeira geração de cada versão por instância
(ou depois de publicar): o custo é pago uma vez, fora do caminho quente.

## Concorrência, invalidação e falha de dependência

Testado em `Phase11ResilienceTests` e `GenerationLimitsTests`:

- **Troca de versão sob carga** (duas instâncias, mesmo banco, 8 clientes):
  publicação da v2 e rollback para a v1 durante a carga — ~5.700 gerações,
  **todas** com conteúdo coerente com a versão informada (nunca mistura);
  as duas instâncias passaram a servir a nova versão em milissegundos
  (Change Stream), bem dentro do limite de 3 s do teste; depois de uma
  resposta com a versão nova, nenhum pedido seguinte recebeu a antiga.
- **MongoDB derrubado**: chave aquecida segue gerando (20 gerações em
  ~30 ms); só o primeiro pedido após o vencimento do cache de credencial paga
  a tentativa no banco (~2 s no teste, `ServerSelectionTimeout`); chave fria
  responde `503` + `Retry-After: 5` em ~2 s; `/health/ready` = `Degraded`.
- **Limites de entrada**: profundidade 33+, mais de 10.000 nós e texto acima
  de 10.000 caracteres → `400` sem ecoar o dado, conferidos no JSON antes
  de montar a árvore; corpo acima de 1 MiB → `413` em qualquer grafia do
  caminho (`/xml`, `/XML`, `/xml/`).
- Na janela curta da suíte (menos de 100 amostras) o teste confere o p95 —
  com 50 amostras o p99 é o próprio máximo e uma pausa do SO o reprova; a
  execução registrada (60 s, ≥ 200 amostras) confere o p99.
- Os limiares de tempo dos testes ganham folga de 4× com `CI=true`
  (máquina compartilhada); as metas formais valem para a execução acima.

## Capacidade e dimensionamento

- **Uma instância** sustenta o pico dimensionado (200/min) com uso de CPU
  desprezível e a margem de 10× (2.000/min) com p99 de poucos milissegundos.
  Réplicas adicionais são para **disponibilidade** (falha de instância,
  deploy sem parada), não para vazão: recomendação mínima de **2 instâncias**.
- Estimativa: vazão ≈ `instâncias × teto medido` (runtime sem estado, cache
  por instância). Em produção, meça de novo com o template real — templates
  com muitas coleções/itens custam mais que a DPS de exemplo; o benchmark de
  execução por template dá a ordem de grandeza (p99 × taxa por segundo ≪ 1
  núcleo).
- **Memória**: ~150–170 MB de working set por instância com um template
  aquecido; cada versão compilada em cache soma o tamanho do modelo
  compilado (dezenas a centenas de KB para layouts reais). O cache cresce
  com as versões efetivamente usadas (fallback limitado a 5 por pedido).
- **Primeira geração** de cada versão por instância paga a compilação
  (~3–13 ms no exemplo; XSDs grandes levam mais) — não há fila nem
  pré-aquecimento obrigatório.

## Gargalos e riscos aceitos

- Latência medida em processo: não inclui rede entre máquinas, TLS nem
  gateway. Some o RTT da sua rede aos percentis.
- O template de exemplo é pequeno; o limite superior real depende do XSD e
  do mapeamento do cliente (ver "Capacidade").
- Sem fila, cache distribuído ou microserviço (seção 25: sem evidência) — a
  medição não mostrou gargalo que justifique.
- O log de console em nível Information por geração custa CPU sob
  saturação; em produção use o exportador OTLP e nível Warning para o
  console se a vazão por instância importar.
