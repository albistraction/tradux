# ADR-0015: Contrato de origem por XSD e entrada em XML

- **Status**: aceito
- **Data**: 2026-09-30
- **Fase**: 12 — Contrato de origem XSD e entrada XML

## Contexto

A ideia central do produto, reafirmada pelo usuário depois da Fase 11, é:
**o autor escolhe um XSD de origem; o integrador envia um XML nesse
formato; a plataforma mapeia para o XML do XSD de destino**. O requisito de
origem (seção 10) aceita "XSD completo ou pacote de XSDs" como contrato de
origem, mas diz que as instâncias chegam em JSON ou objeto C#; as fases
anteriores seguiram essa leitura literal. Não existia contrato de origem
persistido (ADR-0009/0011 registram isso como lacuna aceita): a árvore de
origem era inferida de um JSON de exemplo, e nenhum caminho aceitava XML de
entrada. Esta ADR corrige a leitura: **XML de origem → XML de destino é o
fluxo principal**; JSON/objeto C# continua como entrada alternativa.

## Decisão

### 1. Onde vive o XSD de origem

- `SnapshotContent.SourceSchema` (arquivos, ponto de entrada, raiz
  opcional), opcional. Faz parte do hash do snapshot **só quando presente**:
  o hash de todo snapshot anterior não muda (teste com a fórmula antiga).
- Camadas: a **base** pode trazer `SourceSchema` (produto/cliente não, como
  o XSD de destino); entra no hash da camada só quando presente; o
  resolvedor o leva ao conteúdo resolvido. Persistido no MongoDB (snapshot e
  camada) e viaja no pacote `.traduxpkg` (conteúdo e camadas).
- Snapshot compilado (`CompiledSnapshot.Source`) guarda o XSD de origem já
  importado e compilado — o caminho quente não relê nada.

### 2. Forma da origem derivada do XSD (única fonte da regra)

`SourceShape` (projeto Xsd) decide o que é objeto, lista ou valor, **a
partir do XSD**, e é usada tanto pelo leitor de XML quanto pela árvore de
campos do editor — os caminhos que o editor oferece são exatamente os que a
geração resolve:

- elemento complexo = objeto; elemento simples = valor;
- repetição vem do XSD (`maxOccurs > 1` do elemento ou de um grupo que o
  contém), nunca da contagem num XML: coleção com um item continua lista;
- atributo = campo `@nome` (com o tipo do atributo no XSD, igual a elemento
  simples); texto de conteúdo simples com atributos = `$value` (inclusive
  na raiz; mesmo nome do valor de item de lista simples);
- grupos (sequence/choice/all) não têm nível próprio;
- tipos: numéricos (decimal, integer e derivados, float, double) → número
  (`decimal`, cultura invariante); `date`/`dateTime`/`gYearMonth` → data
  (texto ISO, como no JSON); o resto → texto;
- tipos recursivos param no segundo nível; `xs:any` é convertido pela
  estrutura do próprio XML (`xsi:nil` inválido ali não é nulo — nunca erro 500);
- o mesmo nome local em dois ramos de `choice` (ou repetido no conteúdo)
  vira **um** campo de lista — editor e leitor coerentes;
- teto de 20.000 campos (`SourceShape.MaxFields`) na forma: tipos que se
  desdobram exponencialmente num XSD pequeno são recusados na importação.

A importação do XSD de origem (`XsdSchemaImporter.ImportSourceInMemory`)
reaproveita **a mesma carga segura** do destino (limites de arquivos e
bytes, nomes relativos, DOCTYPE proibido, nada fora do conjunto, checagem de
patterns perigosos), mas **não** as restrições do modelo de destino: o
destino continua aceitando só os tipos que o motor sabe gerar.

### 3. Leitura do XML de origem

`SourceXmlReader`: valida contra o XSD de origem compilado **antes** de
mapear (XML fora do XSD não gera nada), com DTD proibido, sem resolvedor
externo, limite de caracteres e de profundidade; confere o elemento raiz e o
namespace; aplica também `xs:unique`/`xs:key`/`xs:keyref`; converte para o
mesmo dicionário que o JSON produziria (nomes
locais; `xsi:nil` = presente e nulo; ausente ≠ nulo). Lido de bytes, respeita
a codificação declarada no XML (ex.: ISO-8859-1). Problemas saem com linha e
coluna.

### 4. Superfícies

- **API**: `POST /v1/organizations/{org}/templates/{t}/operations/{op}/transform`
  (`template.use`), corpo = XML de origem; query `version`, `versions`
  (fallback), `correlationId`; mesma resposta de `POST …/xml`. XML fora do
  XSD → 200 com `SOURCE_XML_INVALID` (linha/coluna); versão sem XSD de
  origem → `TEMPLATE_WITHOUT_SOURCE_SCHEMA`; corpo limitado a 1 MiB pelo
  endpoint (413); limites de profundidade/nós/texto da ADR-0014 valem sobre
  a árvore convertida. No fallback, cada versão lê o XML com o próprio XSD
  de origem.
- **SDK**: `TraduxRuntime.TransformXmlAsync(TransformXmlRequest)` (e,
  portanto, `TraduxPackageRuntime` offline); `TraduxDesigner.DescribeSourceSchema`.
- **Desenho**: `POST /v1/design/source-schema` (árvore de campos no formato
  do editor); `POST /v1/design/test` aceita `sourceSchema` + `sourceXml`.
- **Publicação**: `POST …/publications` e criação de camada aceitam
  `sourceSchema`; cenário funcional aceita `sourceXml` (entrada em XML) —
  o gate funcional e a suíte estrutural (semente convertida para JSON) usam
  o XSD de origem; mensagem de falha de cenário XML traz só linha/coluna.
- **Editor**: passo 2 "Entrada (contrato de origem)" com modo **XML (XSD de
  origem)** como padrão — carrega o XSD de origem (campos vêm dele), XML de
  exemplo para teste e para o cenário da publicação; modo JSON continua.
  Rascunhos antigos com JSON abrem no modo JSON.

### 5. XSDs reais de NFS-e (correção pós-fase, 2026-10-01)

Primeiro uso real: pacote da NFS-e Nacional 1.01 (`DPS_v1.01.xsd`,
`NFSe_v1.01.xsd`, `tiposComplexos`, `tiposSimples`, `xmldsig-core-schema.xsd`).
Com estas correções, DPS e NFSe importam **como origem e como destino**
(verificado com os arquivos reais, que não são versionados aqui):

- **Vários arquivos no editor**: a escolha de XSDs acumula entre seleções,
  com remoção por arquivo (origem e destino).
- **DOCTYPE do xmldsig oficial do W3C**: nas cargas em memória e ZIP,
  `SafeDoctype` (leitura linear, sem regex) remove o DOCTYPE e expande
  **apenas** entidades internas com valor literal sem `&`, `%` ou `<`,
  fora de comentários e CDATA, com o limite de bytes do arquivo conferido
  **durante** a expansão (nada acima do limite é montado em memória) e o
  limite total do pacote reconferido depois. `ATTLIST`/comentários são
  ignorados; identificador externo nunca é buscado; entidades predefinidas
  não podem ser redeclaradas; codificação diferente de UTF-8/ISO-8859-1 com
  DOCTYPE é recusada. Entidade `SYSTEM`/`PUBLIC`, referência a entidade de
  parâmetro, `ELEMENT`, `NOTATION`, PI, dois DOCTYPEs: recusados (XXE,
  seção 11). `ImportSingle` (disco, SDK) segue estrito.
  Também: só o DOCTYPE do **prólogo** vale; valor de entidade não pode ter
  aspas nem `>` (sem injeção de atributo); bytes inválidos para a
  codificação declarada são recusados (nunca trocados por U+FFFD);
  `ATTLIST` é descartado (defaults de DTD não são aplicados — o xmldsig não
  depende deles); cardinalidade acima de `int` vira erro de schema (400).
  Limite registrado: `ref` para outro namespace usa só o nome local — mapeado,
  sairia no namespace do destino e a validação XSD reprova (nunca em silêncio).
- **Assinatura no destino**: `ref` (`<xs:element ref="ds:Signature"/>`)
  usa o nome resolvido; `xs:any` e conteúdo que o modelo de destino não
  expressa (conteúdo simples com atributos, vazio) viram **conteúdo opaco**:
  aparece no editor, só leitura; opcional e não mapeado é omitido (DPS);
  obrigatório gera `MAPPING_MISSING_REQUIRED` — **nunca** sai em silêncio
  (ex.: `NFSe` exige `ds:Signature`: como destino, a NFSe não publica até
  existir assinatura pelo ponto de pós-processamento do conector externo).
- **Tipos simples no destino**: além de decimal/date/gYearMonth (formatação
  própria), qualquer tipo simples (integer, dateTime, boolean, base64…) é
  escrito como o texto recebido; a validação XSD do XML gerado aplica o tipo.
- **Raiz automática**: com vários elementos globais, os do XML-DSig importado
  não contam; sobrando um, ele é a raiz (`DPS_v1.01.xsd` → `DPS`); senão a
  mensagem lista os candidatos.
- **Limite conhecido do destino**: o motor ainda não gera `xs:choice`. No
  DPS 1.01 há 3 obrigatórios dentro de choice (`infDPS/prest`,
  `serv/locPrest`, `valores/trib/totTrib`) — o editor os mostra bloqueados e a
  publicação de um template que gere DPS fica impedida até o suporte a choice.

## Alternativas consideradas

- **Converter XML em JSON fora da plataforma** (no integrador): rejeitada —
  perde validação pelo XSD de origem, deixa a regra de listas/atributos com
  cada cliente e não dá árvore de campos tipada ao editor.
- **Inferir a árvore de um XML de exemplo** (como o JSON): rejeitada —
  repetição e tipos viriam da amostra, não do contrato; um item só viraria
  objeto e quebraria na primeira nota com dois itens.
- **XSLT gerado a partir do mapeamento**: rejeitada — duplicaria o motor
  (CLAUDE.md: uma só implementação de geração) e perderia regras, gates e
  diagnósticos por XPath.
- **Mudar o importador de destino para aceitar mais tipos**: rejeitada nesta
  fase — mudaria o comportamento do motor na geração; a origem tem modo
  próprio e tolerante.

## Consequências

- O fluxo XML → XML passa pelos mesmos gates, cache, fallback, auditoria,
  telemetria e limites da geração por JSON.
- Templates sem XSD de origem continuam funcionando só por JSON (e recusam
  XML com diagnóstico claro).
- Riscos aceitos: namespaces reduzidos a nomes locais (dois elementos de
  mesmo nome local em namespaces diferentes no mesmo nível viram um campo);
  nomes locais com ponto não são endereçáveis por caminho; conteúdo misto é
  ignorado; `xs:double`/`xs:float` fora da faixa de `decimal` (ex.:
  `1e-40`) arredondam para a precisão de `decimal`, e `xs:token`/
  `normalizedString` chegam como escritos (sem colapsar espaços); um
  template publicado sem cenário em XML pode ganhar uma nova versão de base
  sem XSD de origem e deixar de aceitar XML (quem chama `/transform` recebe
  `TEMPLATE_WITHOUT_SOURCE_SCHEMA`) — mantenha um cenário em XML; a
  expansão exponencial de tipos no importador de **destino** (anterior à
  Fase 12) segue sem teto — registrado como dívida; tipos recursivos
  além do segundo nível não aparecem na árvore do editor (o leitor ainda os
  converte); as mensagens do validador de XML (idioma do runtime) podem
  conter valores do próprio XML enviado — vão só para quem enviou, nunca
  para logs, auditoria ou mensagens de gate.
