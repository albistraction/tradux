# ADR-0008: Fachadas API/SDK sobre snapshot publicado — runtime, conteúdo autocontido e convergência de entrada

- **Status**: aceito
- **Substituído em parte por**: ADR-0025 (a fachada SDK, 2026-11-16; a entrada por objeto C# do §5, 2026-11-17)
- **Data**: 2026-09-28
- **Fase**: 5 — API, MongoDB, cache e SDK público

## Contexto

A Fase 5 precisa expor o motor como API HTTP e como SDK "com resultados
equivalentes", persistir snapshots no MongoDB e servir a geração com cache
aquecido sem ler o banco (ADR-0004, ADR-0005). Várias escolhas concretas
ficaram em aberto nos ADRs da Fase 1 e em `docs/PROJECT_STATE.md` (dúvida
aberta 2: o SDK pode referenciar `Infrastructure.MongoDb`?), e outras só
apareceram na implementação: como JSON e objeto C# convergem para a mesma
árvore, o que exatamente o snapshot guarda, como versões são numeradas e
quais códigos HTTP a API devolve.

## Decisão

1. **Um único caso de uso de geração.** `Tradux.Application.Generation.XmlGenerationService`
   é o único ponto de geração a partir de snapshot. A API
   (`POST /v1/organizations/{org}/templates/{template}/operations/{op}/xml`)
   e o SDK (`Tradux.Sdk.TraduxRuntime`) só convertem a entrada
   para `SourceDataTree` e chamam esse serviço; o mapeamento
   motor→contrato também é único (`GenerationResultMapper`).
2. **SDK sem dependência de infraestrutura (resolve a dúvida aberta 2).**
   `TraduxRuntime` recebe um `ITemplateSnapshotRepository` do
   consumidor. O SDK referencia `Application`, nunca
   `Infrastructure.MongoDb`; quem quiser MongoDB instala o pacote do
   adaptador separadamente e injeta `MongoTemplateSnapshotRepository`
   (e, opcionalmente, `MongoSnapshotChangeFeed` para invalidação). O
   adaptador cria seus índices únicos sozinho antes da primeira publicação
   de cada instância — a garantia "um só ativo" não depende de o host
   lembrar de chamar `EnsureIndexesAsync` (achado B2 da revisão). A seta
   "SDK -. opcional .-> MONGO" de `CONTAINERS.md` passa a significar
   "o *host* que embute o SDK pode ligar o adaptador", não uma referência
   do pacote SDK. Protegido por `Tradux.ArchitectureTests`.
3. **Snapshot autocontido inclui os XSDs.** O documento de
   `compiledSnapshots` guarda os bytes dos arquivos XSD de destino, o
   mapeamento e as tabelas de lookup; o cache-miss é uma leitura por chave
   seguida de `XsdSchemaImporter.ImportInMemory` + `TemplateCompiler` —
   nenhuma outra coleção nem disco. O mapeamento é gravado como texto JSON
   canônico (não sub-documento BSON) porque a conversão JSON→BSON mudaria a
   escala de decimais (`10.50`→`10.5`) e quebraria o round-trip exato e o
   hash.
4. **Versão de snapshot = inteiro sequencial por chave**
   (`organizationId:templateId:operation`), atribuído pelo repositório na
   transação de publicação, mais `contentHash` (SHA-256 sobre hash do XSD,
   raiz, mapeamento canônico e lookups ordenados). `nome@MAJOR.MINOR`
   (ADR-0003) continua valendo para contratos, schemas e camadas; o
   snapshot publicado é identificado pela sequência + hash, que é o que
   ADR-0004 já previa (`snapshotVersion`, `contentHash`). Publicar conteúdo
   com o mesmo hash da versão ativa é idempotente (não cria versão) — a
   checagem roda dentro da transação, então publicações concorrentes do
   mesmo conteúdo convergem para uma única versão.
5. **Convergência de entrada — por construção.** *Um objeto C# e a sua
   serialização pelo `System.Text.Json` (opções padrão) produzem exatamente
   o mesmo resultado* (JSON pela API, objeto pelo SDK) — **porque são o
   mesmo caminho**: as fachadas públicas do SDK (`TraduxRuntime`,
   `TraduxClient.GenerateXml`) convertem o objeto com
   `SourceDataTree.FromClrObject`, que serializa com o `System.Text.Json`
   e lê com `FromJson`, igual a um JSON recebido pela API. A equivalência
   deixou de depender de cada operador tratar cada tipo CLR (quatro rodadas
   de revisão acharam uma divergência nova de datas a cada rodada quando
   isso era feito operador a operador). Cobertura: gate
   (`Phase5GateTests`, API real × SDK) e matriz tipo × expressão × tipo de
   destino (`EquivalenceContractMatrixTests`, 660 casos, mensagens
   incluídas). Consequências fixadas:
   - Números viram `decimal` (nunca `double`; fora da faixa é erro);
     `enum` vira número (padrão do `System.Text.Json`); datas viram texto
     ISO-8601 (`DateTime` com `Z`/fuso conforme o `Kind`); objetos
     aninhados viram dicionários, listas viram listas. Nomes de caminho são
     exatos (sensíveis a maiúsculas/minúsculas).
   - Vale a semântica do `System.Text.Json` para o objeto de origem:
     propriedades com `[JsonIgnore]` e campos públicos não entram na árvore;
     propriedade declarada como tipo base serializa só os membros do tipo
     base (membros da classe derivada não aparecem — declare o tipo
     concreto ou use `[JsonDerivedType]`); chaves de dicionário não-string
     viram texto; ciclos, `NaN`/infinito e tipos não serializáveis, assim
     como exceções lançadas por getters, viram `ArgumentException`.
   - Item escalar de coleção expõe o valor em `$value` (além de `$index`).
   - Custo aceito: uma serialização JSON por chamada do SDK com objeto C#
     (medir na Fase 11).
   - Regras do motor para texto ISO (valem também para `TraduxClient`
     com literais em memória): comparação (`equals`/`notEquals`/
     `greaterThan`/`lessThan`) como instante `DateTimeOffset` quando os dois
     lados são data ou texto ISO (`yyyy-MM-dd`, `yyyy-MM`,
     `yyyy-MM-ddTHH:mm:ss[.f][Z|±hh:mm]`; sem fuso = UTC), senão texto ×
     texto ordinal; `formatDate` aceita texto ISO; operadores de texto e
     chave de `lookup` usam o texto canônico (datas = texto do
     `System.Text.Json`); `xs:date`/`gYearMonth` normalizam texto ISO para
     a forma do tipo. Efeito aceito: dois textos que parecem datas ISO são
     comparados como datas (`"2026-09"` = `"2026-09-01"`).
   - **Nenhum erro de conversão vira exceção**: valor que não cabe no tipo
     do elemento (`"abc"`/`"1500,50"` em `xs:decimal`, objeto/lista em
     `xs:string`) é `VALUE_CONVERSION_FAILED` crítico com XPath; objeto/
     lista em operador de texto é `EXPECTED_SCALAR`; falha de .NET dentro
     de operador (formato inválido, estouro) é `RULE_EXECUTION_FAILED`.
     Texto numérico em `xs:decimal` só aceita sinal e ponto decimal — sem
     separador de milhar, para `"1500,50"` nunca virar `150050`. Mensagens
     de diagnóstico usam nomes neutros de tipo ("texto", "número",
     "data"…), nunca nomes CLR.
   - JSON escrito à mão numa forma diferente da do `System.Text.Json`
     continua equivalente em comparações de data, `formatDate` e emissão de
     `xs:date`/`gYearMonth`; em operadores de texto o texto é usado como
     enviado.
   - **A publicação valida a forma persistida**: `SnapshotPublisher`
     serializa e desserializa o conteúdo (`SnapshotJson.Rehydrate`) antes de
     compilar, calcular o hash e gravar; o conversor de literais grava
     datas no mesmo texto canônico.
6. **Semântica HTTP da geração.** 200 sempre que a geração executou,
   inclusive com falha de regra/XSD (`success=false` + diagnósticos — o
   relatório é o produto); 404 quando não há snapshot para a
   chave/versão (corpo = o mesmo `GenerationResult` com
   `TEMPLATE_SNAPSHOT_NOT_FOUND`); 400 (`application/problem+json`) para
   pedido malformado. `durationMs` é número em milissegundos; severidade é
   texto.
7. **Política de versão da Fase 5:** versão ativa (padrão) ou uma versão
   fixada. `versionsAttempted` tem exatamente uma tentativa. Fallback entre
   candidatas fica para a Fase 7, sem mudar o formato do contrato.

## Alternativas consideradas

- **Equivalência JSON × objeto C# garantida operador a operador** (cada
  operador e a emissão tratando `DateOnly`/`DateTime`/`DateTimeOffset`/
  texto ISO): tentada durante a Fase 5 e abandonada — cada rodada de
  revisão achou outro caminho divergente (comparação, fuso, `formatDate`,
  texto, `lookup`, `xs:string`, `Kind`). Passar o objeto pelo mesmo
  caminho do JSON elimina a classe inteira de defeito.
- **Normalizar o JSON para tipos C# (texto ISO → `DateOnly`…)**: exigiria
  saber, sem o contrato de origem tipado, quais textos são datas —
  rejeitada.
- **SDK chamando a API por HTTP**: garantiria equivalência trivialmente,
  mas contraria o requisito de SDK em processo (seção 26, "sem rede") e
  duplicaria latência — rejeitada.
- **SDK referenciando `Infrastructure.MongoDb` diretamente**: mais cômodo
  para quem usa MongoDB, mas força o driver em todo consumidor do SDK e
  fura a regra "fachadas não carregam infraestrutura" — rejeitada.
- **Snapshot referenciando `destinationSchemas` por id em vez de embutir
  XSDs**: menor documento, mas o cache-miss passaria a exigir duas leituras
  e dependeria da imutabilidade de outra coleção — rejeitada (ADR-0004 pede
  uma leitura).
- **Números JSON como `double`**: perde precisão em valores fiscais —
  rejeitada.
- **Nomes de caminho case-insensitive**: amigável a JSON camelCase, mas
  ambíguo (`a` e `A` no mesmo objeto) e divergente do objeto C# —
  rejeitada; um contrato de origem define os nomes uma vez.
- **422 para geração com `success=false`**: mistura "pedido inválido" com
  "dado de negócio reprovado pelo XSD", que é resultado normal do produto —
  rejeitada.

## Consequências

- Equivalência API/SDK é estrutural (mesmo código), e ainda assim coberta
  por teste ponta a ponta contra MongoDB real (`Phase5GateTests`).
- Um processo que não registrou um operador customizado usado por um
  snapshot devolve `TEMPLATE_SNAPSHOT_COMPILATION_FAILED` (sem cache da
  falha) em vez de quebrar; a publicação já recusa operador desconhecido
  no processo publicador.
- Documentos de snapshot crescem com o tamanho dos XSDs. Débito aceito: o
  limite padrão de pacote (`XsdImportSecurityOptions.MaxPackageTotalBytes`,
  20 MB) é maior que o limite de documento do MongoDB (16 MB); um conjunto
  de XSDs acima de ~16 MB compila, mas a gravação falha com erro do driver.
  Pacotes fiscais reais ficam muito abaixo disso; revisar (limite
  explícito na publicação ou GridFS) se aparecer um caso real.
- Clientes que enviam JSON em camelCase precisam usar os nomes do contrato
  de origem; normalização de nomes, se algum dia for necessária, é uma
  camada explícita no contrato de origem, não um afrouxamento do motor.
- A API ainda não tem autenticação/autorização (Fase 9): a organização vem
  da rota e é confiada. Não expor fora de rede confiável até lá.
