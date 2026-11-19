# ADR-0016: Contrato de entrada salvo, versionado e estendido na árvore

- **Status**: aceito
- **Substituído em parte por**: ADR-0025 (catálogo, rotas e tela de contratos de origem; 2026-11-16)
- **Data**: 2026-10-01
- **Fase**: 13 — Contrato de entrada versionado com extensões

## Contexto

Uso real: o ERP envia a DPS da NFS-e Nacional com um bloco próprio
(`infErp`) dentro de `<DPS>`. O XSD oficial não prevê o bloco. O usuário
quer, no editor, carregar os XSDs oficiais e **montar manualmente na árvore**
os blocos/campos próprios; esse **modelo de entrada** precisa ser **salvo**
para servir a vários DE-PARAs e ser **versionado**. O modelo de domínio da
Fase 1 (`docs/domain/DOMAIN_MODEL.md` §3.2–§4) já prevê "contrato de origem"
composto por módulos versionados (ex.: `dps@1.x` + um módulo adicional); até
aqui (ADR-0015) o XSD de origem vivia só dentro de cada template.

## Decisão

1. **Contrato de entrada** (`SourceContract`), isolado por organização:
   identificador estável (`contractId`), nome, **versões imutáveis
   sequenciais** (1, 2, 3…). Cada versão guarda: XSD base (arquivos, arquivo
   principal, raiz opcional — o "módulo" oficial, ex.: DPS 1.01) e a lista de
   **extensões** montadas no editor, mais hash de conteúdo, autor e data.
   Salvar um conteúdo idêntico ao da última versão não cria versão nova.
   Simplificação do modelo da Fase 1: um módulo XSD base + extensões
   manuais, versão exata (sem faixa `1.x` nesta fase).
2. **Extensão** = nó novo pendurado num objeto da árvore do XSD base (ou de
   outra extensão), por caminho de nomes locais (`""` = raiz;
   `infDPS.serv` = dentro de `serv`; dentro de lista, o caminho passa pelo
   nome da lista e vale para cada item). Nó: nome (NCName), **campo**
   (texto/número/data) ou **bloco** (com filhos), obrigatório, repetível.
   Nome não pode repetir um filho existente.
3. **Validação do XML de entrada** com extensões: o XML é lido com a mesma
   carga segura (DTD proibido, limites); os elementos de extensão são
   validados pelas regras da extensão (presença, repetição, tipo, sem filho
   desconhecido, profundidade). O restante é validado pelo XSD base numa
   passagem de um leitor que **pula** as subárvores de extensão; o documento
   não é alterado. Depois tudo é convertido com a forma combinada (base +
   extensões). A posição do bloco dentro do pai é livre. O namespace dos
   elementos de extensão é o dos elementos locais do pai: o do pai quando o
   XSD é qualified, e nenhum quando é unqualified. O XSD oficial nunca é
   reescrito.
4. **Template usa uma versão exata**: o editor escolhe contrato + versão; o
   snapshot recebe uma **cópia** (XSD base + extensões + referência
   `contractId@versão` para rastreio) — continua autocontido (ADR-0004) e o
   hash inclui as extensões (só quando houver; hashes antigos não mudam). Uma
   versão nova do contrato não altera templates publicados; para usá-la, o
   autor republica.
5. **Superfícies**:
   - API (`/v1/organizations/{org}/source-contracts…`): listar contratos,
     listar versões, obter uma versão (com arquivos e extensões) —
     `template.read`; criar versão — `template.edit`; outra organização → 404;
     criação auditada.
   - Desenho: `POST /v1/design/source-contract` (XSD + extensões → árvore com
     os nós de extensão marcados, ou 400 explicando o erro); teste de rascunho
     e publicação aceitam `sourceExtensions` (+ `sourceContract` para rastreio).
   - SDK: `TraduxSourceContracts`.
   - Editor: no modo XML, "Contrato de entrada": abrir um contrato salvo
     (escolhendo a versão) ou criar a partir de XSDs; botões "+ campo" e
     "+ bloco" nos objetos da árvore; editar/remover nós de extensão; "Salvar
     nova versão".

## Alternativas consideradas

- **Gerar um XSD combinado** (redefine/override ou extensão de tipo): a
  extensão de tipo só acrescenta no fim da sequência (o bloco ficaria depois
  da assinatura), `xs:redefine` é frágil com includes/imports reais e
  `xs:override` não é suportado pelo .NET; rejeitada.
- **Inferir a extensão do XML de exemplo**: oferecida; o usuário escolheu
  montagem manual (controle explícito do contrato).
- **XSD separado só do bloco**: oferecida; não escolhida.
- **Contrato referenciado (não copiado) pelo snapshot**: quebraria o
  snapshot autocontido e a execução offline por pacote.

## Consequências

- O mesmo contrato serve a vários templates; versões são auditáveis e
  imutáveis.
- Riscos aceitos: elementos de extensão no namespace do pai (sem namespace
  próprio); a ordem dos elementos de extensão no pai não é imposta; tipos de
  extensão limitados a texto/número/data (sem pattern/enumeração nesta fase).

## Adendo — revisão independente (rodada 1)

- **Leitura limitada.** Profundidade e quantidade de elementos
  (`SourceXmlLimits.MaxElements`) são aplicadas durante a leitura, antes de
  montar a árvore. Nenhuma validação recursiva vê uma entrada sem limite.
- **Validação em duas partes, sem alterar o documento.**
  - As extensões são validadas pelas regras do contrato.
  - O restante é validado pelo XSD base numa única passagem de um leitor que
    pula as subárvores de extensão, identificadas pela posição de início.
  - Os elementos de extensão ficam no namespace do pai e não aceitam
    atributos (nem `xsi:nil`/`xsi:type`).
  - Elementos de conteúdo simples não recebem extensões.
- **Referência conferida.** Na publicação, `sourceContract` precisa existir na
  organização e ter o mesmo hash do conteúdo enviado. O hash usa a forma
  canônica das extensões.
- **Quebra de API registrada.** `SourceValueType` passou de
  `Tradux.Xsd` para `Tradux.Domain.SourceContracts`, porque é
  usado pelo contrato público. Quem consumia o pacote da Fase 12 ajusta o
  `using`.

## Adendo: revisão independente (rodada 2)

- **Namespace real de cada nó.** Cada nó da forma guarda o namespace com que
  o elemento aparece no XML (qualified, unqualified ou `ref` de outro
  namespace). O caminho até o pai e os elementos de extensão são procurados
  por esse nome qualificado.
- **Custo linear.** As extensões são agrupadas por pai e cada caminho é
  percorrido uma vez. A busca para ao atingir o teto de problemas reportados.
- **Referência conferida na Application.**
  - `ContentPublicationService` recebe o repositório de contratos e confere
    `SourceSchema.Contract` na API e no SDK
    (`TraduxRuntime(..., sourceContracts:)`). Divergência gera
    `SOURCE_CONTRACT_REFERENCE_INVALID`.
  - Sem repositório, a referência é descartada.
  - Na importação de pacote, a referência é sempre descartada, porque é da
    organização de origem.
