# ADR-0014: Performance, resiliência e prontidão operacional

- **Status**: aceito
- **Data**: 2026-09-30
- **Fase**: 11 — Performance, resiliência e prontidão

## Contexto

Seção 25: dimensionar para 100 a 200 gerações por minuto e testar com
margem de dez vezes o pico; nenhuma leitura do MongoDB com cache aquecido;
runtime sem estado; templates e XSDs pré-compilados; escala horizontal;
limites de tempo, memória, profundidade e entrada; benchmarks separados de
compilação e execução; métricas de percentis; "não introduza filas ou
microserviços sem evidência". Fase 11: teste a 200/min, margem a
2.000/min, percentis, CPU, memória e cache, testes de concorrência e
invalidação, runbooks de falha e rollback, revisão final. Gate: "metas
atingidas ou gargalos medidos e aceitos formalmente; operação e recuperação
documentadas".

Estado herdado: cache local de snapshot compilado com invalidação por
Change Streams + poll de segurança (ADR-0005); limite só de corpo em
`/v1/design`; entrada de geração limitada apenas pela profundidade padrão
do `System.Text.Json` (64) e pelo corpo padrão do Kestrel (30 MB); cliente
MongoDB com timeouts padrão do driver (seleção de servidor 30 s); sem
endpoints de saúde.

## Decisão

### 1. Medição (como provamos as metas)

- **Carga em modelo aberto** (taxa de chegada fixa, sem esperar a resposta
  anterior — o modelo fechado esconde fila): a API real (`Program` de
  produção) servida por **Kestrel com sockets reais**
  (`WebApplicationFactory.UseKestrel`), MongoDB real (replica set de um nó),
  credencial de runtime, geração de um template publicado com cache aquecido.
- **Cenários**: 200/min (pico dimensionado) e 2.000/min (margem 10×), cada
  um com aquecimento separado da janela medida. Na suíte padrão a janela é
  curta (15 s na taxa real: 50 e 500 gerações); a execução completa
  (`TRADUX_LOAD_SECONDS=60`) é a registrada em `docs/operations/PERFORMANCE.md`.
  Mais um teste de **saturação** em modelo fechado (N clientes em laço) para
  medir o teto de uma instância.
- **Métricas registradas**: p50/p90/p95/p99/máx de latência no cliente,
  taxa atingida, erros, CPU do processo (% do total de núcleos), memória
  (working set, heap gerenciado, bytes alocados por geração, coletas de
  gen2), acertos de cache (contador `tradux.cache.lookups` da telemetria da
  Fase 9) e **comandos de leitura no MongoDB durante a janela medida**
  (instrumentação do driver — prova direta, não contador nosso).
- **Benchmarks separados** de compilação de snapshot e de execução do motor
  (sem HTTP), com aquecimento, para localizar gargalo por etapa.
- Relatórios em JSON em `artifacts/perf/` (fora do controle de versão) e
  resumo no log do teste.

### 2. Metas formais (por instância)

| Métrica | 200/min | 2.000/min |
|---|---|---|
| Erros | 0 | 0 |
| p99 | ≤ 100 ms | ≤ 250 ms |
| Comandos na coleção de snapshots durante a janela aquecida (invalidação desligada no teste) | 0 | 0 |
| Acerto de cache na janela | 100% | 100% |
| Crescimento de working set na janela | ≤ 64 MB | ≤ 64 MB |

"Nenhuma leitura do MongoDB com cache aquecido" (seção 25) é medido **por
pedido**: nenhum comando na coleção de snapshots enquanto a carga roda. Em
produção existem leituras periódicas de fundo, independentes da taxa de
pedidos: poll de segurança dos ponteiros de versão ativa (30 s), revalidação
de cada credencial em uso (a cada 30 s), atualização das janelas de captura
diagnóstica (15 s) e o Change Stream. O teste desliga a invalidação para que
a contagem isole o caminho de geração.

Saturação: teto medido de uma instância ≥ 2.000/min (a margem precisa
caber numa instância só; escala horizontal é folga adicional). Os limiares
de latência são folgados para máquina de desenvolvimento/CI compartilhada —
o número medido é o que vai para o documento de performance.

### 3. Limites de entrada da geração (seção 25)

`GenerationLimits` (Application), aplicado em `XmlGenerationService` —
**ponto único para API e SDK**, então as duas fachadas recusam igual:

- profundidade máxima da árvore de origem: **32**;
- nós máximos (objetos + itens de coleção + valores): **10.000**;
- tamanho máximo de texto por valor: **10.000** caracteres.

Violação → `GenerationInputLimitException` (`ArgumentException`): API
responde `400` com o limite violado (sem ecoar o dado); SDK lança. Além
disso, corpo de `POST …/xml` limitado a **1 MiB** (`413`), configurável em
`Tradux:Generation:MaxRequestBytes`, aplicado ao **endpoint roteado** (não ao
texto do caminho — `/XML` e `/xml/` também). Configuração em
`Tradux:Generation:Limits`. Para JSON, os limites são conferidos sobre o
`JsonElement` **antes** de montar a árvore interna (entrada gigante não é
alocada).

Caminhos cobertos: `POST …/xml` e `TraduxRuntime` (inclusive
fallback e `TraduxPackageRuntime`, que usam o padrão), `POST /v1/design/test`
e `TraduxDesigner.Test`, `TraduxClient.GenerateXml` (padrão).
Fora dos limites, de propósito: os cenários funcionais e a suíte estrutural
dos **gates de publicação** (entrada do autor, com `template.publish`,
limitada pelo corpo de `/v1/design` e da publicação) e
`/v1/design/quality` (só dados sintéticos gerados pelo próprio sistema).

Tempo e memória por geração ficam limitados por construção: a execução é
linear no tamanho da entrada (limitada acima) e no template compilado; toda
regex tem timeout (ADR-0012 §9); o tamanho do cache é limitado pelo número
de versões publicadas efetivamente usadas (a lista de fallback já é
limitada a 5). Não há timeout por geração — a execução é síncrona e sem E/S
e o pior caso medido fica registrado.

### 4. Resiliência

- **MongoDB indisponível**: a geração com cache aquecido continua (nenhuma
  dependência no caminho quente — testado derrubando o `mongod`). Chave
  fria falha rápido: `ServerSelectionTimeout` padrão de **5 s** (configurável
  em `Tradux:MongoDb:ServerSelectionTimeout`; `serverSelectionTimeoutMS` na
  string de conexão prevalece) e a API responde **`503` com `Retry-After`**
  em vez de `500`.
- **Change Stream indisponível**: já coberto (ADR-0005) — nova tentativa
  com poll de reconciliação; o poll de segurança limita a defasagem.
- **Credenciais com o MongoDB fora**: credencial já validada pela
  instância continua aceita até `Tradux:Security:CredentialStaleGrace`
  (padrão 10 min) além da validade do cache (30 s), com nova tentativa no
  banco no máximo uma vez por validade. Só falha de **disponibilidade**
  (`CredentialStoreUnavailableException`, traduzida no adaptador MongoDB)
  libera a carência; qualquer outro erro sobe. Consequência aceita: numa
  **partição** (só esta instância sem banco), uma revogação feita em outra
  instância leva até validade + carência para valer aqui; numa queda total
  ninguém revoga. Isto atualiza a afirmação "revogação em até 30 s" do
  ADR-0012 §5 para "até 30 s, ou até 30 s + carência numa instância sem
  acesso ao banco". Sem a carência, 30 s de queda do banco derrubariam toda
  geração, inclusive a aquecida.
- **Classificação do 503**: `TimeoutException` do driver (exceto
  `RegexMatchTimeoutException`), `MongoConnectionException`,
  `MongoNotPrimaryException`, `MongoNodeIsRecoveringException` (eleição) e
  `CredentialStoreUnavailableException`, inclusive embrulhadas.
- **Saúde**: `GET /health/live` (processo de pé) e `GET /health/ready`
  (ping no MongoDB com timeout curto). MongoDB fora deixa o *ready* em
  `Degraded` (HTTP 200), **não** `Unhealthy`: a instância ainda serve o
  tráfego aquecido, e tirá-la do balanceador transformaria uma falha
  parcial em total. Anônimos, fora de `/v1`, sem detalhe além do status.
- **Concorrência e invalidação**: testadas sob carga — publicação e
  rollback durante carga em duas instâncias: toda resposta é inteiramente
  de uma versão (XML e versão reportada coerentes) e as duas instâncias
  convergem para a versão nova dentro do limite de defasagem.

### 5. O que não fazemos (seção 25: sem evidência)

Sem fila, sem microserviço, sem cache distribuído: a medição mostra se uma
instância sustenta a margem; se não sustentar, o gargalo medido é
registrado e aceito ou tratado aqui antes de qualquer infraestrutura nova.

## Alternativas consideradas

- **BenchmarkDotNet**: rigoroso, mas um projeto e um executor à parte e
  execução longa; a pergunta da fase é "a instância sustenta a taxa com que
  latência", respondida pela carga em modelo aberto. Benchmarks por etapa
  com cronômetro e aquecimento bastam para localizar gargalo.
- **Ferramenta de carga externa (k6, JMeter)**: exige instalação e
  orquestração fora da suíte; a carga em processo com Kestrel real roda em
  qualquer máquina com o SDK e cabe no gate automatizado.
- **Readiness `Unhealthy` com MongoDB fora**: rejeitada (ver §4).
- **Timeout por geração com cancelamento cooperativo**: rejeitado por ora —
  execução em memória, linear e sem E/S; os limites de entrada limitam o
  pior caso.

## Consequências

- A suíte padrão ganha testes de carga curtos (dezenas de segundos); a
  execução longa é manual e registrada.
- Latência medida em processo não inclui rede real entre máquinas nem TLS;
  o documento de performance diz isso e dá a fórmula de capacidade.
- Pedidos que antes passavam (corpo > 1 MiB, árvore > 32 níveis) passam a
  ser recusados — mudança de contrato documentada em `docs/api/README.md`.
