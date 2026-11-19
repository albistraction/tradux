# Estado do projeto

> Fonte de verdade operacional. Se este arquivo divergir do estado real do
> repositório, corrija-o antes de continuar qualquer trabalho.

## Fase ativa

**Fase 22 — o produto é só o microsserviço tradutor (ADR-0025).** Implementada e
commitada em 2026-11-16 (99b27ab). Em 2026-11-17/18 o usuário pediu a refatoração
"untangle-codebase" (com remoção de comentários e testes inúteis): 4 ondas commitadas
(e32211e, 3d5a31e, e2da5d9 e a onda 4), decisões em ADR-0025 §8. Itens 1–5 do gate
verificados; falta o item 6: revisão independente (agente `reviewer`).

Em 2026-11-19 o projeto foi renomeado para **Tradux** (ADR-0026): `Nfse.Mapping.*` →
`Tradux.*`, configuração `Tradux:`, credencial `tradux_`, banco padrão `tradux-mapping`.

## Objetivo atual

Fechar a Fase 22: revisão independente e decidir com o usuário o que fazer com os
templates de teste que o e2e grava na base de desenvolvimento.

## Concluído

- Repositório git inicializado (estava vazio, sem repositório).
- Requisito de origem registrado em
  `docs/requirements/PROMPT_MESTRE_CLAUDE.md`.
- `CLAUDE.md` criado com regras permanentes, limites do núcleo e referências.
- Estrutura `.claude/` criada:
  - 9 agentes especialistas + 1 revisor independente (10 arquivos) em
    `.claude/agents/` (architect, domain-xsd, dotnet-engine, api-sdk,
    mongodb-performance, frontend-rule-builder, security-observability,
    qa-automation, docs-rag, reviewer).
  - 8 skills em `.claude/skills/` (architecture-adr,
    xsd-contract-analysis, rule-operator-design, template-migration,
    test-fixture-generation, security-review, performance-review,
    phase-handoff).
  - 4 arquivos de regra por área em `.claude/rules/` (dotnet, testing,
    security, documentation).
- Estrutura `docs/` criada: `PROJECT_STATE.md`, `ROADMAP.md`,
  `DECISIONS.md`, `REQUIREMENTS.md`, `GLOSSARY.md`,
  `architecture/{SYSTEM_CONTEXT,CONTAINERS,COMPONENTS}.md`,
  `architecture/adrs/TEMPLATE.md`, `domain/`, `api/`, `schemas/`,
  `operations/`, `runbooks/` (com README de propósito), `rag/` (4 arquivos
  de esqueleto).
- `handoffs/CURRENT.md` e `handoffs/archive/` criados.
- Backlog detalhado da Fase 1 produzido em `docs/ROADMAP.md`.
- Revisão independente da Fase 0 executada pelo agente `reviewer` (ver seção
  "Resultado da revisão independente da Fase 0" abaixo). A revisão apontou 2
  achados que impediam o fechamento do gate (commit ausente e contradição
  interna neste arquivo); ambos foram corrigidos após a revisão, nesta mesma
  sessão.
- Commit inicial do repositório criado, cobrindo toda a estrutura da Fase 0.

**Fase 1** (todos os itens do backlog de `docs/ROADMAP.md`, executados em
paralelo por agentes especialistas, cada um restrito a arquivos disjuntos):

- Item 1 — Diagramas C4 completos (agente `architect`): `docs/architecture/
  SYSTEM_CONTEXT.md` (nível 1), `CONTAINERS.md` (nível 2, com a regra
  "Domínio/Motor nunca dependem de infraestrutura" reforçada visualmente por
  arestas "PROIBIDO"), `COMPONENTS.md` (nível 3 do Engine: LayerResolver,
  TemplateCompiler, RuleExecutor, XmlDocumentBuilder, XsdValidator,
  VersionFallbackSelector).
- Item 2 — Modelo de domínio (agente `domain-xsd`): `docs/domain/
  DOMAIN_MODEL.md`, as 23 entidades da seção 9 com atributos/identidade/
  relações, Operação modelada como catálogo aberto, contrato composto por
  módulos versionados, cadeia de camadas até snapshot, cardinalidade×
  nulabilidade como eixos independentes.
- Item 3 — 7 ADRs (agente `architect`, skill `architecture-adr`):
  ADR-0001 (motor compila templates, não interpreta — decisão do usuário,
  formalizada com C++/P-Invoke como alternativa rejeitada), ADR-0002 (.NET
  10 LTS — confiança média, ver dúvidas abertas), ADR-0003 (versionamento
  imutável), ADR-0004 (persistência MongoDB por adaptadores), ADR-0005
  (cache local + invalidação atômica via Change Streams), ADR-0006 (formato
  de camadas), e ADR-0007 (arquitetura geral: Clean Architecture com portas
  em `Application`, DDD pragmático, monólito modular, runtime sem estado —
  adicionado pelo coordenador para cobrir um item do backlog que o lote de
  ADRs originalmente não tinha coberto).
- Item 4 — Contratos preliminares de API/SDK (agente `api-sdk`): `docs/api/
  README.md` com as 11 fachadas da seção 26, contrato de `GerarXml`/
  `ResultadoGeracao`, e o formato do relatório estruturado da seção 18.
- Item 5 — Threat model inicial (agente `security-observability`):
  `docs/operations/THREAT_MODEL.md`, 26 ameaças catalogadas (isolamento,
  upload de XSD, autorização por ação, observabilidade, pacote portátil,
  publicação/rollback) com severidade e fase-alvo de mitigação.
- Item 6 — Fixtures de XSD (agente `domain-xsd`): `docs/schemas/
  exemplo-simples.xsd` (+ análise, alvo da Fase 2) e `docs/schemas/
  exemplo-composto.xsd` + `exemplo-composto-tipos.xsd` (+ análise, alvo da
  Fase 3, com `choice`, coleção repetível e `xs:include`); ambos validados
  como XML bem-formado e compilados com `XmlSchemaSet` do .NET.
- Commit da Fase 1 criado.

**Fase 2** (implementada diretamente pelo coordenador, não por subagente —
ciclo escrever/compilar/testar exigia feedback imediato):

- Solução .NET criada: `Tradux.slnx`, `global.json` (SDK `10.0.400`),
  `Directory.Build.props` (`net10.0`, `Nullable`, `ImplicitUsings`),
  `.gitignore`.
- `src/Tradux.Domain`: `Cardinality`/`Nillability` (eixos
  independentes), `SourceDataTree` (árvore interna tipada — versão plana
  desta fase, com `TryGetValue` distinguindo caminho ausente de valor nulo),
  `FieldMapping`/`MappingTemplate` (mapeamento direto de campo escalar).
- `src/Tradux.Xsd`: `XsdSchemaImporter` (importa 1 XSD via
  `XmlSchemaSet` com `XmlResolver = null` e `DtdProcessing.Prohibit` —
  mitigação XXE mesmo antes da Fase 3 formal), `DestinationSchema`/
  `SchemaElement`/`SchemaAttribute` (modelo canônico: ordem, cardinalidade,
  nulabilidade, tipo escalar via `XmlTypeCode`, default, atributo fixo).
- `src/Tradux.Engine`: `TemplateCompiler` (compila para delegates via
  `System.Linq.Expressions`, ADR-0001 — não interpreta a cada execução),
  `RuleExecutor` (aplica cardinalidade/nulabilidade/default com diagnóstico
  por campo), `XmlDocumentBuilder` (constrói `XDocument` a partir do modelo
  do schema, nunca concatenação de string; formata datas/decimais
  culture-invariant), `XsdValidator` (valida contra o `XmlSchemaSet` exato),
  `XmlGenerationEngine` (orquestrador ponta a ponta).
- `src/Tradux.Contracts`: `Diagnostic`/`DiagnosticSeverity`/
  `GenerationResult` (versão simplificada da seção 18, sem versão/fallback —
  chegam na Fase 5).
- `src/Tradux.Sdk`: `TraduxClient` (fachada mínima —
  `CompileTemplate`/`GenerateXml`, aceita objeto C# direto).
- 22 testes (`dotnet test`, todos passando): 5 em Domain.Tests, 9 em
  Xsd.Tests (contra `exemplo-simples.xsd` real), 8 em Engine.Tests —
  incluindo o "exemplo realista" ponta a ponta que prova o gate: entrada C#
  → XML válido (Success=true, sem diagnóstico crítico), caso nillable
  (`valorDesconto=null` → `xsi:nil="true"`), caso opcional omitido
  (`serie=null` → elemento ausente), caractere especial sobrevivendo ao
  round-trip XML (prova de "não é concatenação de string"), obrigatório sem
  mapeamento (diagnóstico crítico em tempo de compilação), obrigatório nulo
  em runtime (diagnóstico crítico com XPath/SourcePath/sugestão), default
  de schema aplicado quando não mapeado, e violação de `pattern` do XSD
  (CNPJ inválido) capturada no estágio final de validação com XPath
  aproximado e sugestão.
- Um bug real encontrado e corrigido durante a própria escrita dos testes:
  `TemplateCompiler` não criava nenhum binding para elemento obrigatório sem
  mapeamento mas com `default` no schema, então o valor padrão nunca era
  aplicado (o elemento simplesmente desaparecia da saída). Corrigido
  adicionando um binding sintético "sempre ausente" nesse caso, para que
  `RuleExecutor` acione a lógica de default já existente.
- Revisão independente do gate da Fase 2 (agente `reviewer`, ver seção
  "Resultado da revisão independente da Fase 2" abaixo): **GATE CUMPRIDO**
  de primeira, sem achado bloqueante. 2 melhorias recomendadas (não
  bloqueantes) aplicadas mesmo assim: (1) `.slnx` corrigido na documentação
  (o `dotnet new sln` gerou `Tradux.slnx`, formato novo do SDK, não
  `.sln` clássico — 3 referências corrigidas); (2) `XsdValidator` reescrito
  para rastrear o elemento aberto durante a validação via `XmlReader`,
  produzindo `DestinationXPath` aproximado + `Suggestion` também no estágio
  final de validação (antes só repassava a mensagem crua da exceção), com
  um teste novo forçando uma violação real de `pattern` (CNPJ inválido) para
  provar o diagnóstico.

**Fase 3** (implementada diretamente pelo coordenador, mesmo motivo da
Fase 2: ciclo escrever/compilar/testar):

- `Tradux.Xsd` ganhou um modelo canônico completo de árvore de
  partícula (`SchemaParticle`/`ParticleKind` — `Sequence`/`Choice`/`All`,
  aninhável; `ScalarElementItem`/`ComplexElementItem`/`GroupItem`),
  preservando a projeção plana `DestinationSchema.Elements` da Fase 2
  (agora nullable — `null` quando o schema tem `choice`/`all`/elementos
  complexos, sinalizando que o motor da Fase 2 não se aplica e a Fase 4
  deve consumir `RootContent` diretamente). `TemplateCompiler` (Fase 2)
  ganhou uma guarda explícita para esse caso (`NotSupportedException` com
  mensagem clara, não falha silenciosa).
- `XsdSchemaImporter.ImportSingle` agora resolve `xs:include`/`xs:import`
  locais via `SandboxedFileSystemXmlResolver` (permite apenas arquivos
  dentro do diretório do arquivo de entrada; bloqueia URIs remotas,
  path traversal e arquivos com `<!DOCTYPE`).
- `XsdSchemaImporter.ImportPackage` (novo): importa um pacote ZIP
  inteiramente em memória via `InMemoryPackageXmlResolver` (nunca extrai
  para disco — sem superfície real de path traversal), com limites
  configuráveis de quantidade de entradas e tamanho (`XsdImportSecurityOptions`).
- Achado durante os próprios testes: o compilador de XSD do .NET trata
  falha de resolução de `include`/`import` como evento de validação
  recuperável (schema fica silenciosamente incompleto), não como exceção
  propagada — isso mascararia toda mitigação de segurança. Corrigido com
  `AttachResolverFailureSurfacing`, que rethrow a falha real do resolver a
  partir do `ValidationEventHandler`.
- `ImportedXsd.ContentHash` (SHA-256 sobre o conteúdo canônico de todos os
  arquivos efetivamente carregados) — determinismo comprovado por teste
  (mesma importação, mesmo hash; pacote ZIP vs. arquivos soltos idênticos
  produzem o mesmo hash).
- `Tradux.Domain.SourceContracts.SourceModuleReference`: parse/
  validação da convenção `nome@faixaDeVersao` (seção 10, ex.: `dps@1.x`).
- Fixtures novas em `docs/schemas/`: `exemplo-composto-pacote.zip`
  (empacota o par já existente da Fase 1); `exemplo-import-ns-a.xsd` +
  `exemplo-import-ns-b.xsd` (par minimalista para `xs:import` entre
  namespaces diferentes — fecha um gap que a análise da Fase 1 tinha
  deixado registrado, já que o par composto só usa `xs:include`).
- 31 testes novos (53 no total): importação completa do fixture composto
  (choice de 2 ramos, coleção repetível via tipo incluído, atributo
  fixo, nillable-vs-opcional no nível composto), segurança (path
  traversal em include local e em nome de entrada ZIP, URI remota,
  DOCTYPE, limites de tamanho/quantidade), `xs:import` cross-namespace,
  determinismo de hash, e `SourceModuleReference`.
- Commit da Fase 3 criado.

**Fase 4** (implementada diretamente pelo coordenador, mesmo motivo das
Fases 2–3):

- `Tradux.Domain.Mapping` ganhou uma árvore de expressão de regra —
  `RuleNode`/`FieldReferenceNode`/`LiteralNode`/`OperatorNode` — que é
  puro dado de configuração (seção 12/28: nenhum código arbitrário é
  executado). `FieldMapping` evoluiu de `(SourcePath, DestinationElementName)`
  para `(DestinationElementName, Value: RuleNode, Condition: RuleNode?)` —
  `Condition` é o "grupo de regras" da seção 9 (E/OU/negação via os
  operadores `and`/`or`/`not`), que decide SE o mapeamento se aplica.
  `FieldMapping.Direct(...)` preserva a ergonomia do `copy` implícito.
  `MappingTemplate` ganhou `Complex`/`Collections` além de `Fields`, e
  `ComplexMapping`/`CollectionMapping` novos — juntos formam uma árvore que
  espelha `SchemaParticle` (Fase 3), permitindo aninhamento arbitrário
  ("listas aninhadas", seção 14).
- `Tradux.Engine.Operators`: catálogo dos 24 operadores da seção 13
  (`equals` a `omit`), cada um uma entrada data-driven
  (`RuleOperatorDefinition`) que monta uma `Expression.Call` para um método
  estático de `RuleOperatorMethods` — a lógica real é só métodos C#
  simples e testáveis; nenhum despacho acontece em runtime (ADR-0001
  continua valendo: tudo isso é compilado UMA VEZ). `RuleOperatorCatalog.
  Register` permite adicionar operadores sem tocar em nenhum arquivo do
  núcleo — testado registrando um operador fictício inteiramente dentro
  de um teste.
- `TemplateCompiler` (reescrito): agora percorre `SchemaParticle` e
  `MappingTemplate` juntos, recursivamente, produzindo uma árvore
  compilada (`CompiledParticle`/`CompiledScalarBinding`/
  `CompiledComplexBinding`/`CompiledCollectionBinding`). Elementos com
  conteúdo `xs:choice`/`xs:all`: se não mapeados, caem no mesmo
  diagnóstico "obrigatório sem mapeamento" de sempre; se alguém tentar
  mapeá-los mesmo assim, a compilação falha alto e claro
  (`NotSupportedException`) — nunca produz XML incorreto silenciosamente.
  Essa é a fronteira documentada e deliberada desta fase (ver "Dívida
  técnica aceita").
- `RuleExecutor`/`XmlDocumentBuilder` (reescritos, recursivos): a
  distinção obrigatório/opcional/nillable/default da Fase 2 é preservada
  intacta para escalares; elementos complexos e coleções seguem a mesma
  árvore recursivamente; erros de operador (`RuleOperatorException`)
  viram diagnóstico crítico com XPath/origem, nunca derrubam a geração
  inteira. `SourceDataTree.FromObject` ganhou suporte a valores sintéticos
  extras — usado para expor `$index` (seção 14: "contexto local do item")
  dentro de cada iteração de coleção.
- Fixture nova `docs/schemas/exemplo-colecao.xsd`: coleção repetível SEM
  `choice` (isola a prova de geração de coleções da fronteira de choice
  ainda não suportada — `exemplo-composto.xsd`, Fase 3, tem os dois
  juntos, então serve para importação mas não para geração isolada).
- 34 testes novos (87 no total): 24 operadores testados diretamente
  (`RuleOperatorMethodsTests`); operadores compostos/lookup/condição/
  extensibilidade de ponta a ponta via `TraduxClient` real
  (`RuleEngineGenerationTests`); coleções — 3 itens, 0 itens, filtro,
  `$index`, origem não-enumerável — contra `exemplo-colecao.xsd`
  (`CollectionGenerationTests`); fronteira de choice contra
  `exemplo-composto.xsd` — não mapeado gera diagnóstico limpo, mapeado
  explicitamente falha alto (`ChoiceBoundaryTests`). Zero regressão nas
  Fases 2–3.
- Revisão independente do gate da Fase 4 (agente `reviewer`, ver seção
  "Resultado da revisão independente da Fase 4" abaixo): **GATE CUMPRIDO**
  de primeira (nenhum achado bloqueante). 3 melhorias recomendadas
  aplicadas mesmo assim: (1) o guard "elemento com conteúdo xs:choice/
  xs:all só pode ser mapeado com erro alto e claro" agora também cobre o
  elemento raiz (antes só cobria elementos aninhados — gap real que o
  revisor reproduziu com um XSD scratch; nenhum fixture do repositório
  chegava a exercitá-lo); (2) operador `constant` registrado
  explicitamente no catálogo (antes só existia implicitamente via
  `LiteralNode` — a contagem "24 operadores" já documentada agora bate
  exatamente com o catálogo real); (3) teste novo cobrindo erro de tipo
  dentro de uma `Condition` (antes só havia cobertura desse tipo de erro
  dentro de `Value`).
- Commit da Fase 4 criado (`aba6636`).

**Fase 5** (implementada diretamente pelo coordenador; decisões em
ADR-0008):

- Divergência corrigida ao assumir a sessão: este arquivo e o handoff
  diziam "Fase 4 pendente de commit", mas ela já estava commitada; havia
  esqueletos não documentados da Fase 5 no working tree.
- `Tradux.Domain`: `RuleNode` serializável (`System.Text.Json`,
  polimorfismo `$type`, `LiteralValueJsonConverter` preserva o tipo CLR de
  literais; `OperatorNode.Of` substitui o construtor `params`).
  `SourceDataTree.FromJson` (convergência de entrada: objeto→dicionário,
  array→lista, número→`decimal`, nomes exatos) e `FromObject` aceitando
  dicionário (itens de coleção vindos de JSON).
- `Tradux.Xsd`: `XsdSchemaImporter.ImportInMemory` — mesmas
  proteções do ZIP (nomes sem `..`/absolutos, limites, DOCTYPE proibido,
  nada fora do conjunto), mesmo hash do pacote equivalente.
- `Tradux.Engine`: objeto JSON onde se espera coleção vira
  `COLLECTION_SOURCE_NOT_ENUMERABLE` (antes seria iterado como pares
  chave/valor).
- `Tradux.Contracts`: `GenerationResult` ganhou `VersionUsed`,
  `VersionsAttempted` (`VersionAttempt`), `CorrelationId`, `Metadata`;
  `durationMs` numérico; severidade como texto; `GenerateXmlRequest`,
  `TemplateVersionInfo`.
- `Tradux.Application` (novo): `TemplateKey`; portas
  `ITemplateSnapshotRepository`/`ISnapshotChangeFeed`; snapshot
  autocontido (`SnapshotContent` com bytes dos XSDs, mapeamento, lookups);
  `SnapshotCompiler` (+ hash de conteúdo determinístico);
  `CompiledSnapshotCache` (Lazy por chave, falha/não-encontrado fora do
  cache, troca de referência inteira na invalidação, versões fixadas
  imutáveis); `SnapshotCacheInvalidator` (Change Stream com reconexão +
  poll de segurança só do ponteiro); `XmlGenerationService` (único caso de
  uso de geração para API e SDK); `SnapshotPublisher` (publicação mínima:
  recusa conteúdo que não compila ou tem diagnóstico crítico; idempotente
  por hash); `GenerationResultMapper` (único mapeamento motor→contrato).
- `Tradux.Infrastructure.MongoDb` (novo): `MongoTemplateSnapshotRepository`
  (publicação em transação multi-documento, retry em conflito de índice
  único, índices `ux_key_version`, `ux_key_active` parcial — o banco recusa
  dois ativos —, `ix_org_status`, `ix_content_hash`; toda query filtra
  pela chave com organização); `MongoSnapshotChangeFeed` (Change Streams
  com `$project` só de campos de chave).
- `Tradux.Api` (novo, substituiu o template `weatherforecast`):
  `POST /v1/organizations/{org}/templates/{template}/operations/{op}/xml`
  (200/404/400), `GET …/versions`, OpenAPI em `/openapi/v1.json`,
  ProblemDetails, índices criados no startup, invalidação em
  `BackgroundService`. Configuração em `Tradux:MongoDb`/`Tradux:Cache`.
- `Tradux.Sdk`: `TraduxRuntime` (gera por JSON ou objeto C#,
  publica, lista versões, roda invalidação; repositório injetado — o SDK
  não referencia infraestrutura). `TraduxClient` passou a usar o
  mapeamento compartilhado.
- Empacotamento: `Directory.Build.props` torna todo `src/` exceto a API
  pacote NuGet `0.5.0` com documentação XML; `dotnet pack` gera 7 pacotes
  e o nuspec do SDK depende só de Application/Contracts/Engine/Xsd.
- Testes (106 novos; 200 no total, após as correções da revisão): `Application.Tests` (34 — matriz de equivalência de 660 casos, cache
  quente sem leitura, cold-start concorrente com leitura única, troca de
  entrada, poll, Change Stream com e sem falha, publicador, equivalência
  JSON×objeto, versão fixada, isolamento por organização, operador
  ausente, 200 gerações concorrentes); `Api.IntegrationTests` (32, MongoDB
  real via `EphemeralMongo`, replica set de 1 nó — repositório,
  concorrência de publicação (inclusive banco sem índice e conteúdo idêntico), índice parcial, Change Stream, contrato
  HTTP, OpenAPI e o gate: equivalência API×SDK em 11 casos — JSON×JSON, JSON×objeto C# e `System.Text.Json(objeto)`×objeto, com regras e textos sobre datas — e zero
  comandos ao MongoDB com cache quente, medido no próprio driver);
  `ArchitectureTests` (13, novo — limites de dependência por referência
  compilada transitiva e por `.csproj`, com controle negativo); Domain
  (+8), Xsd (+6) e Engine (+13, operadores de data/escalares da revisão).
- Docs: ADR-0008; `docs/api/README.md` §8; `docs/operations/MONGODB_E_CACHE.md`;
  `CONTAINERS.md` (seta SDK→infra); `DECISIONS.md`; `CLAUDE.md`.

- Commit da Fase 5 criado (`918718c`).

**Fase 6** (implementada diretamente pelo coordenador; decisões em
ADR-0009; autorizada pelo usuário na mesma sessão da Fase 5):

- Domínio: caminhos com ponto (`Tomador.Cnpj`) navegam objetos aninhados em
  `SourceDataTree` (JSON e objeto C#; chave exata com ponto tem
  precedência; intermediário nulo = presente e nulo).
- Motor: `RuleOperatorMetadata` (categoria, descrição, parâmetros com tipo,
  resultado) para os 24 operadores + genérico para customizados;
  `RuleOperatorCatalog.Describe()` e `Register(definition, metadata)`.
- Contratos `Contracts.Design` + `Application.Design.DraftDesignService`:
  listar operadores, descrever a árvore de destino de XSDs em Base64
  (importação em memória com as proteções da Fase 3; choice/all e grupos
  marcados como não mapeáveis), testar rascunho (compila, lista
  obrigatórios sem mapeamento, gera, compara XML esperado semanticamente;
  erros viram diagnóstico, nunca exceção).
- API: `GET /v1/design/operators`, `POST /v1/design/schema`,
  `POST /v1/design/test`; CORS por configuração (`Tradux:Cors:AllowedOrigins`).
  SDK: `TraduxDesigner` (mesmo serviço).
- `src/Tradux.Web` (novo): host estático + `/config.js` + CSP
  restrito; editor em módulos ES sem build/npm — árvores de origem (inferida
  da amostra, escopo por bloco `for each`) e destino (status ✔/⚠/○/⛔,
  badges, resumo da regra, contagem de erros), DE-PARA por clique ou
  arraste, valor por campo/constante/transformação aninhada/omitir/nulo com
  padrão, condição estilo query builder (E/OU/NÃO, grupos aninhados), bloco
  `for each` (coleção, filtro, ordenação, aninhável), tabelas DE-PARA,
  teste com XML esperado e comparação lado a lado, diagnósticos clicáveis
  que selecionam o campo, rascunho em `localStorage` + exportar/importar.
- Testes: Domain (+8: caminhos com ponto), Engine (+2: metadados), 
  Application (+11: `DraftDesignServiceTests`), Api (+5:
  `DesignApiTests`, incluindo o JSON exato do editor e CORS),
  ArchitectureTests (+1: Web sem `ProjectReference`), `Tradux.Web.Tests`
  (novo: roda 10 testes `node --test` dos módulos puros) e o script de
  navegador `e2e/golden-path.mjs` (fora do `dotnet test`: exige API,
  editor e MongoDB rodando).
- **Gate verificado no navegador** (Edge headless via DevTools, 2026-09-29):
  XSD enviado como arquivo → dados de exemplo → `numero` por clique,
  `valorTotal` por arraste → `for each` sobre `Itens` com filtro
  `ValorUnitario > 0` no query builder → campos do item no escopo do item →
  "Testar rascunho": válido, XML contém os itens filtrados; transformação
  `concat($index, ": ", Descricao)` montada no editor de valor aparece no
  XML; remover `numero` marca ⚠ e o diagnóstico `MAPPING_MISSING_REQUIRED`
  seleciona o campo. Nenhum JSON de template editado. Achado durante a
  verificação e corrigido: o `<input type="file">` recriado a cada
  renderização escondia os arquivos escolhidos — agora listados no painel.
- Docs: ADR-0009, `docs/api/README.md` §9, backlog no `ROADMAP.md`,
  `DECISIONS.md`, `CLAUDE.md`.

- Commit da Fase 6 criado (`b4f58fd`).

**Fase 7** (implementada diretamente pelo coordenador; decisões em ADR-0010;
usuário autorizou seguir todas as fases em sequência):

- Domínio `Layers`: `LayerKind`, `LayerRef` (com organização), `LayerOverride`
  (`Set`/`Remove` por caminho lógico), `MappingEntry` (escalar ou for each),
  `LookupOverride`.
- Application `Layers`: `TemplateLayer`/`NewLayerVersion` (imutável,
  append-only), `TemplateBinding` (política, cenários, linhagem),
  `LayerResolver` (resolução com história por caminho e conflitos
  críticos/avisos), `MappingPaths` (template ⇄ mapa por caminho, mesma
  regra do editor), `ResolutionDiff`, gates plugáveis (`CompilationGate`,
  `FunctionalScenarioGate`), `LayeredTemplateService` (criar camada, vincular
  e publicar, propagar com rebase do topo, política manual/automática,
  aprovar/rejeitar, rollback, explicar, auditar). `SnapshotManifest` gravado
  no snapshot (entra no hash). `XmlGenerationService.GenerateWithFallbackAsync`
  (fallback por lista ordenada, erros por versão). `DestinationTreeBuilder` e
  `XmlSemanticComparer` extraídos para uso comum (editor + camadas).
- MongoDB: `templateLayers`, `templateBindings`, `templateUpdates`,
  `auditEvents` + `manifestJson` e `ActivateVersionAsync` (rollback
  transacional) no repositório de snapshots.
- SDK: `TraduxAdministration`. API: `versions` no corpo de geração
  (fallback), `GET …/versions/{v}/explanation`, `GET …/updates`.
- ROADMAP: publicação pelo editor e escrita por HTTP movidas para a Fase 9
  (dependem de autorização).
- Testes: Application (+15: cadeia de 3 camadas com dono diferente,
  explicação de toda regra, atualização compatível automática com rebase,
  atualização que reprova cenário e base nova sem elemento sobrescrito pelo
  cliente — ambas **sem alterar a versão ativa** —, proposta manual com
  diferenças e aprovação, rejeição/substituição, rollback auditado,
  fallback, proteção de camadas, auditoria sem payload, conflitos do
  resolvedor); Api.IntegrationTests (+4, MongoDB real: gate pela API,
  endpoint de explicação, atualização e rollback chegando ao cache pelo
  Change Stream, round-trip das coleções novas).
- Docs: ADR-0010, `docs/api/README.md` §10, `MONGODB_E_CACHE.md`,
  `DECISIONS.md`, backlog no ROADMAP.

### Fase 8 — Qualidade automatizada (concluída; gate cumprido na 3ª rodada)

- XSD: `ScalarFacets` em `SchemaElement` (tamanhos, enumeração, patterns por
  passo de derivação, limites, dígitos), lidos do `XmlSchemaSet` compilado
  (`XsdFacetReader`).
- `Application/Quality`: `SourceProfiler` (perfil de origem inferido do
  mapeamento + XSD: destinos diretos, dicas de tipo pelos operadores, chaves
  de lookup, predicados), `PatternValueGenerator` (subconjunto de regex XSD,
  menor/maior valor), `SyntheticValues`, `StructuralSuiteBuilder` (conjunto
  fixo: obrigatórios, completo, limites, enumerações, repetições, nulos,
  inválidos por restrição; choice anotado como não coberto) e
  `StructuralTestRunner` → `QualityReport` (Contracts, sem dados).
- Gates: `PublicationGatePipeline` (compilação → `StructuralTestGate` →
  `FunctionalScenarioGate`); cenário funcional exige XML esperado ou
  asserções (`StructuralAssertion` Exists/Absent/Equals/Count).
  `ContentPublicationService` (SDK direto, auditado, condicionado à versão
  ativa) fecha a dívida W1; `SnapshotPublisher` virou `internal`.
- Atualização de camadas: `TemplateUpdate.Report` e `Regressions` (saída por
  cenário, ativa × proposta).
- Achados reais da suíte: (1) o motor arredondava decimais em silêncio
  (`"0.00"` fixo) — agora não descarta dígito significativo e o XSD reprova
  casas a mais; (2) a tabela DE-PARA do fixture não cobria a situação "3"
  da enumeração; (3) a regra de datas do fixture comparava campo opcional
  ausente (erro crítico) — regra corrigida com `setDefault`.
- API `POST /v1/design/quality`, SDK `TraduxDesigner.CheckQuality`;
  editor com botão "Testes estruturais" e painel (`model/quality.js`,
  `ui/qualityPanel.js`); etapa 9 no e2e.
- Testes: Xsd (+4 facetas), Engine (+1 decimal), Application (+23 suíte e
  pattern/perfil, +23 gate/asserções/comparação semântica), integração (+2,
  MongoDB real: recusa não chega à API e é auditada; endpoint de qualidade),
  Node (+1).
- Docs: ADR-0011, `DECISIONS.md`, `docs/api/README.md` §9/§11, backlog no
  ROADMAP (tabela de status corrigida — Fases 5–7 constavam "Não iniciada").

### Fase 9 — Segurança, autorização e observabilidade (concluída; gate cumprido na 4ª rodada)

- `Application/Security`: permissões da seção 20, papéis (`admin`,
  `runtime`, `editor`, `auditor`), `AccessPrincipal`, credenciais por
  chave de API (segredo mostrado uma vez, só SHA-256 guardado,
  `CredentialService` com cache em memória e limite de entradas;
  emissão/revogação auditadas); `MongoApiCredentialStore`.
- API: esquema `ApiKey`, política `perm:<permissão>` em toda rota `/v1`
  (fallback exige autenticação), filtro de organização (outra org → 404
  sem corpo), credenciais iniciais por configuração (só hash).
- Escrita por HTTP (`AdministrationEndpoints`): camadas, vínculo,
  publicação por camadas, publicação de conteúdo (editor), configuração,
  rollback, aprovação/rejeição, auditoria; ator = principal. Teste e
  qualidade de desenho auditados (`template.test`).
- Dívidas da Fase 7: A6 (transição condicional `TransitionAsync`, estado
  `Applying`), W3 (`ActivateVersionAsync` condicionado), W7 (máx. 5
  versões no fallback). XSD: `PatternSafetyChecker` recusa quantificador
  > 1000, pattern > 1000 caracteres e quantificador ilimitado sobre grupo
  quantificado.
- Observabilidade: `TraduxTelemetry` (spans/métricas de geração, cache e
  publicação, só códigos), log estruturado por geração, OpenTelemetry na
  API (OTLP opcional).
- Captura diagnóstica: `DiagnosticCaptureService` (janela em memória,
  mascaramento por nome sensível, limite por janela, auditoria) +
  `MongoDiagnosticCaptureStore` (AES-GCM, TTL).
- Editor: credencial só na sessão, painel "5. Publicar" (cenário = dados de
  exemplo + XML esperado; recusa mostra diagnósticos e relatório); e2e
  com etapas 0 (credencial) e 10 (publicação recusada e aceita).
- Testes: Application (+10: papéis, credenciais/cache/revogação,
  mascaramento, captura, A6, W3), Xsd (+16 segurança de pattern),
  integração (+12: toda rota com política, 401 sem credencial, runtime só
  gera, papéis, revogação, isolamento 404 em toda rota, escrita por HTTP,
  **varredura de logs/traces/métricas sem valor fiscal**, captura
  diagnóstica cifrada/TTL/auditada, sem chave → 503, W3 contra mongod),
  Node (+2).
- Docs: ADR-0012, `DECISIONS.md`, `docs/api/README.md` §12, backlog no ROADMAP.

### Fase 10 — Pacotes, RAG e documentação operacional (concluída; gate cumprido na 2ª rodada)

- Snapshot guarda os cenários que o aprovaram (fora do hash); camadas ganham
  importação de versão exata; regra do hash do snapshot num único lugar
  (`SnapshotPublisher.SnapshotHash`).
- `Application/Packaging`: formato `.traduxpkg` (manifesto com SHA-256 por
  entrada, JSON canônico, ZIP com data fixa), leitura com validação
  (limites, zip bomb, caminhos, hashes, entradas não declaradas, segredos),
  `PackageService` (exportar; importar com camadas exatas, hash de camada
  recalculado, publicação pelos gates, conferência do hash do snapshot,
  auditoria), `PackageSnapshotRepository` (execução offline).
- SDK `TraduxPackages` e `TraduxPackageRuntime`; API `GET …/package`
  (`template.export`) e `POST /v1/organizations/{org}/packages`
  (`template.import`).
- `src/Tradux.Knowledge` (sem referência a nenhum projeto da
  solução): modelo com metadados da seção 24, chunking por tipo, índice BM25
  persistível (porta `IKnowledgeIndex`), indexação incremental por hash com
  remoção de obsoletos por escopo, filtro de tenant/nível de acesso,
  registro `docs/rag/sources.json`, avaliador (precisão@k, cobertura).
- API `GET /v1/knowledge/search` (`template.read`; restrito exige
  `audit.read`). Teste de arquitetura: núcleo/Application/SDK não alcançam o
  RAG e ele não alcança o motor.
- Runbooks (`docs/runbooks/`, 7) e guia do consumidor
  (`docs/api/CONSUMERS.md`); `docs/rag/*` atualizados; avaliação:
  cobertura 1,00, precisão@5 0,60 (limiares 0,90/0,50).
- Testes: Application (+9 pacote: ida e volta com hashes iguais, promoção
  para outra organização, execução offline, adulteração/entrada não
  declarada/caminho/não-ZIP, segredos na exportação e importação, conflito
  de camada e organização, integridade de camada, gates com cenários do
  pacote, offline recusa conteúdo adulterado), Knowledge (+6), integração
  (+3: ida e volta por HTTP entre dois bancos, busca com fonte/versão e
  acesso, permissões/pacote inválido), arquitetura (+2).
- Docs: ADR-0013, `DECISIONS.md`, `docs/api/README.md` §13, backlog no ROADMAP.

### Fase 11 — Performance, resiliência e prontidão (concluída; gate cumprido na 2ª rodada)

- Limites de entrada da geração (`GenerationLimits`: profundidade 32,
  10.000 nós, texto 10.000) no ponto único `XmlGenerationService` (API e
  SDK iguais, inclusive fallback); `SourceDataTree.Measure` (Domain, pura);
  corpo de `POST …/xml` ≤ 1 MiB (413).
- Resiliência: `MongoDbOptions.ServerSelectionTimeout` (5 s) e
  `CreateClientSettings`; MongoDB indisponível → `503` + `Retry-After`;
  `/health/live` e `/health/ready` (MongoDB fora = `Degraded`);
  credencial já validada aceita com o armazenamento fora
  (`CredentialStaleGrace`, 10 min) com nova tentativa no máximo uma vez por
  validade (sem pagar o timeout a cada pedido).
- Carga: `LoadRunner` (modelo aberto e fechado; percentis, CPU, memória,
  GC, cache por telemetria, comandos do MongoDB por coleção) sobre Kestrel
  real (`WebApplicationFactory.UseKestrel`). Resultados de 60 s: 200/min p99
  17,3 ms; 2.000/min p99 4,3 ms; 0 erros; 0 leituras de snapshot; 100% de
  acerto; memória estável; saturação de uma instância ~128–268 mil/min.
  Benchmarks: compilação p50 3,3 ms; execução p50 0,08 ms.
- Concorrência/invalidação: publicação e rollback sob carga em duas
  instâncias — conteúdo sempre coerente com a versão, convergência em
  milissegundos, sem volta à versão antiga; MongoDB derrubado: chave
  aquecida gera, fria `503` rápido, ready `Degraded`.
- Testes: Application (+8: limites, SDK igual à API, credencial com
  armazenamento fora), integração (+7: 200/min, 2.000/min, saturação,
  benchmarks, troca de versão sob carga, MongoDB fora, limites por HTTP).
- Docs: ADR-0014, `DECISIONS.md`, backlog no ROADMAP,
  `docs/operations/PERFORMANCE.md`, runbook `FALHAS_E_RECUPERACAO.md`,
  INCIDENTES/INSTALACAO/ROLLBACK/README atualizados, `docs/api/README.md`
  §14, registro e avaliações do RAG (19 perguntas: cobertura 1,00,
  precisão@5 0,57).

### Fase 12 — Contrato de origem XSD e entrada XML (concluída; gate cumprido)

- Xsd: `XsdSchemaImporter.ImportSourceInMemory` (mesma carga segura do
  destino, sem as restrições de tipo do destino), `SourceShape` (objeto,
  lista pelo `maxOccurs`, atributos `@nome`, `$value`, tipos
  número/data/texto, recursão limitada, `xs:any`), `SourceXmlReader`
  (validação pelo XSD, DTD proibido, limites, raiz/namespace, codificação
  declarada, posições).
- Application: `SnapshotContent.SourceSchema` (hash só quando presente),
  `CompiledSnapshot.Source`, `XmlGenerationService.TransformAsync` (núcleo
  compartilhado com a geração por JSON; fallback lê com o XSD de cada
  versão), `SourceInputs`, `FunctionalScenario.SourceXml` nos gates e na
  regressão de atualizações, camada base com `SourceSchema`.
- MongoDB (snapshot e camada), pacote `.traduxpkg` e `TraduxPackageRuntime`.
- API: `POST …/transform`, `POST /v1/design/source-schema`,
  `sourceSchema`/`sourceXml` no teste de rascunho, publicação e camada;
  limite de corpo pelo endpoint. SDK: `TransformXmlAsync`,
  `DescribeSourceSchema`.
- Editor: passo 2 "Entrada (contrato de origem)" com modo XML como padrão
  (XSD de origem → campos; XML de exemplo → teste e cenário da publicação).
- Fixtures `docs/schemas/origem-exemplo.xsd`/`.xml`. Testes: Xsd (+9),
  Application (+10), integração (+3), JS do editor (+3), e2e no navegador
  (passo 11: fluxo XML → XML até a API).
- Docs: ADR-0015, `DECISIONS.md`, ROADMAP, `docs/api/README.md` §15,
  `CONSUMERS.md`, README dos schemas, registro/avaliação do RAG.

### Correção pós-Fase 14 (2026-10-02): XSD gigante ou patológico não trava nem derruba a API

- **Pedido do usuário.** Um XSD grande "não pode demorar 20 s".
- **Causa.** A compilação de XSD do .NET explode em CPU com blocos enormes ou
  com reuso, e estoura a pilha com cadeias longas ou com tipo recursivo no
  destino.
- **Defesas** (detalhes no adendo do ADR-0017):
  - `XmlDepthGuard`;
  - `ReferenceGraphGuard`: altura de referências até 256, iterativa;
  - `ContentModelSizeChecker`: elementos e atributos por tipo e no total,
    ciclos e `xs:redefine`;
  - erros de schema relançados;
  - tipo recursivo recusado no destino;
  - thread com pilha de 64 MB.
- **Revisões independentes:**
  - Rodada 1: REPROVADO. Achou cinco contornos (C1–C5) e erros engolidos
    (A3).
  - Rodada 2: REPROVADO. Achou cadeias de outros tipos de referência, a
    ordem de declaração (que contornava a profundidade), tipo recursivo no
    destino e atributos fora da conta.
  - Rodada 3: REPROVADO. Achou árvore expandida funda ou exponencial (o
    editor caía depois da importação), union exponencial, curingas, XPath de
    identidade e tipo recursivo virando 500 no snapshot.
  - Tudo foi corrigido, com limite direto na árvore montada
    (`ExpandedTreeLimits`, 128 níveis e 20 mil nós) e limites de
    union/curinga/XPath.
  - Rodada 4: REPROVADO. Achou três caminhos de vários GB: restrições de
    identidade dentro dos limites, `union` anônima em atributo e testes
    estruturais com coleções aninhadas (3^n).
  - Corrigido com:
    - custo combinado das chaves;
    - `union` limitada em todo tipo anônimo, 64 por tipo e 20 mil no total;
    - orçamento de dados sintéticos por caso;
    - mensagem de XSD sem raiz.
  - Rodada 5: REPROVADO. Achou:
    - chaves de identidade em escopos aninhados (minutos de CPU);
    - pattern ambíguo repetido em muitos valores (32 s com 3,7 KB);
    - suíte estrutural com `minOccurs` alto (41 s, 1,6 GB);
    - JSON sintético fundo dando 500.
  - Corrigido com orçamento de tempo total na validação da origem (3 s), na
    geração (5 s) e na suíte (15 s), e com orçamento de nós por caso e na
    suíte. O que estoura falha, nunca aprova.
  - Rodada 6: REPROVADO. Achou dois casos fora do prazo: `union` de
    patterns avaliada numa única leitura (até 86 s) e valores sintéticos da
    suíte fora do relógio. Também apontou que o fallback multiplicava o
    orçamento por versão.
  - Corrigido com:
    - prazo por requisição (8 s, com fallback) e da suíte (15 s, cobrindo
      os valores sintéticos), via `AsyncLocal`, com escopos aninhados
      valendo o menor prazo;
    - regex de 250 ms por avaliação;
    - até 8 avaliações de pattern por valor e por tag;
    - um único diagnóstico de tempo depois que o prazo estoura.
  - Testes: `ContentModelSizeTests` (21) e `XsdExpansionLimitTests` (16).
  - Rodada 7 pendente.
- **Medido.** Os ataques de estrutura são recusados na importação em até
  ~300 ms; os de custo de validação param no prazo da operação (3 a 8 s).
  Nada derruba o processo. O pacote oficial 1.01 importa em 40 a 440 ms,
  como destino e como origem.
- **Limites.** Estão em `XsdImportSecurityOptions`, configuráveis por
  código; a API usa os padrões.

### Fase 21 — Conversor do ERP em C# puro (concluída em 2026-10-13)

O usuário comparou o conversor gerado com o `municipioConversorHelper` feito à mão e recusou
o da Fase 20. O formato ERP (gerador versão 2, ADR-0024) agora gera:
- `Serializacao/{Provedor}/*.cs`: as classes do XSD de saída tipadas como o xsd.exe, com
  `XxxSpecified`. Exceções: `xs:dateTime` vira `string`, para não perder o fuso; enumeração
  vira `string`.
- `Conversores/{Provedor}ConversorHelper.cs`, em C# puro, **sem referência ao motor e sem
  classe de contexto**, compilando em **C# 7.3**:
  - `Para{Provedor}(this Entrada.TCDPS dps, <blocos próprios como parâmetros>)`;
  - inicializadores aninhados;
  - `Montar…` para ramo ou elemento opcional que pode vir vazio;
  - métodos privados no próprio arquivo com a semântica dos operadores do motor.
- Entrada: as classes do contrato de origem do template, no namespace informado
  (`InputNamespace`, `InputRootType`), lidas pela convenção do xsd.exe. Para isso, a forma
  do contrato passou a guardar `XsdType`, `IsEnumeration` e `DeclaredOptional`.
- Não valida, como o feito à mão. Erro de regra lança `InvalidOperationException`.
- Motor: elemento complexo opcional (`minOccurs="0"`) fora de choice só sai com conteúdo.

Fachadas:
- API: `?inputNamespace=` substitui `?inputNamespaces=`;
- CLI: `--input-namespace`;
- editor: campo "Namespace das classes de entrada".

Exemplo do município de exemplo (fora do repositório; dados reais), template `webservice-nfse-erp` **v2**:
- regras pelo manual 2026 do município de exemplo:
  - `Producao` invertido corretamente;
  - datas sem fuso;
  - `Competencia` MM-AAAA;
  - `TipoDeTributacao` por tabela (Simples, imunidade, retenção, local, suspensão, tomadores retentores);
  - `ItemListaServico` 99.99;
  - `Servico2`–`5` zerados;
  - `Tomador.Tipo` 1/2;
  - `ImovelIBSCBS` e `Evento` condicionais;
  - limites de tamanho;
  - `versao="2.00" Id="1"` (fixos na cópia do XSD do exemplo);
- o contrato `DPS_Erp_v1.01.xsd` do exemplo passou a ter os valores do `infErp`
  como `xs:decimal`/`xs:date`, igual à `TInfErp` do ERP;
- o conversor gerado compila em .NET Framework 4.8 / C# 7.3 contra a `TCDPS` real do ERP,
  sem o motor, e o XML sai igual ao do motor (DPS completa e sem `ValorLiquidoNFSe`).

Verificado:
- `dotnet build --no-incremental`: 0 avisos;
- `dotnet test`: 654/654 (Application 268, Engine 65, net48 8/8);
- simplificação a pedido do usuário: operadores `ifElse`, `truncate` e `lookup` com padrão;
  gerador formata constantes, sem `Montar…` para bloco que sempre tem conteúdo, condição
  repetida calculada uma vez. Template do município de exemplo **v3**: `TipoDeTributacao` como se/senão
  (era tabela de 288 linhas), retentores em tabela com padrão, cortes com `truncate`. O
  conversor gerado caiu de ~850 para 615 linhas, compila contra a `TCDPS` e o XML é igual ao do motor;
- `node --test`: 34/34;
- e2e: RASCUNHO OK (publicou a v2);
- exemplo regenerado depois das correções da revisão: compila contra a `TCDPS` real e o XML
  sai igual ao do motor.

### Ajuste pós-Fase 20 (2026-10-13): editor com o visual do Monitor do ERP

- A pedido do usuário, o editor (`src/Tradux.Web/wwwroot`) passou a seguir o visual do
  monitor do ERP consumidor:
  - estrutura topo | menu lateral | conteúdo;
  - painéis de origem no menu lateral verde-água, com barras `#44697c`;
  - conteúdo com borda `#008267`;
  - botões cinza em degradê;
  - tabelas no padrão do grid;
  - mensagens nas cores do Monitor.
- Só HTML/CSS mudaram; os ids e o JS ficaram iguais. Nenhum arquivo do ERP foi copiado: os
  arcos do fundo do menu e o degradê do botão são CSS.
- Verificado: e2e `GATE OK` e `RASCUNHO OK` (rascunho real do ERP), e `node --test` 34/34.

### Fase 20 — Conversor no padrão dos conversores do ERP (concluída em 2026-10-13)

Pedido do usuário: o conversor em arquivo único ("tudo em uma classe")
tinha de ficar igual aos conversores do ERP. Ele escolheu ler a
`TCDPS`/`TInfErp` do ERP.

- **Gerador** `CSharpConverterGenerator.GenerateErp`
  (`Application/CodeGeneration/ErpConverterGenerator.cs`).
  - Saída:
    - `Serializacao/{Provedor}/*.cs`: estilo xsd.exe, uma classe por arquivo;
    - `Conversores/{Provedor}ConversorHelper.cs`: métodos de extensão
      `Para…` por tipo, com as regras em C#;
    - `Conversores/{Provedor}Contexto.cs`;
    - opcional: `Entrada/*.cs`.
  - Opções: provedor, namespaces, namespaces das classes de entrada,
    `InputRootType` e modelo de entrada.
- **Motor** (netstandard2.0):
  - `XsdScalarText`: formatação de escalar, extraída do `XmlDocumentBuilder`
    e usada pelos dois lados;
  - `Conversores/ConversorContexto`: obrigatório, conversão, `Ramo`,
    `Escolha`, `Colecao`, erro de regra → `ConversaoNfseException`;
  - `ValorOrigem` e `ConversorXml`.
- **Xsd**: `ComplexElementItem.TypeName`, `DestinationSchema.RootTypeName`,
  `SourceShapeNode.TypeName`, `SourceShape.RootTypeName`.
- **Fachadas:**
  - SDK: `GenerateErpConverterAsync`;
  - API: `GET …/codegen/erp` (ZIP, `template.read`);
  - CLI: `tradux-codegen --format erp --out <pasta>`;
  - editor: seletor de formato, com o ERP como padrão, e campos de
    provedor, namespaces e classe raiz.
- **Testes:**
  - `ErpConverterEquivalenceTests`: equivalência em C# 12, layout dos
    arquivos e recusa sem XSD de origem;
  - `Phase20GateTests`: API;
  - teste JS do pedido;
  - e2e: passo 10b no arquivo único, R4 no padrão ERP.
- **Exemplo** (fora do repo): `dps-erp-webservice/erp-conversor/`.
  - Gerado pela CLI do template `webservice-nfse-erp`.
  - Compila em net48/C# 12 contra a `LayoutNacional100` e o xmldsig **reais**
    do ERP (linkados, sem alterar o ERP).
  - Roda, e `conferir.mjs` confirma o XML igual ao do motor.

**Gate:** cumprido, depois de duas rodadas de revisão independente (ver
"Resultado da revisão independente da Fase 20" e "Último resultado de testes").

### Fase 19 — Template do ERP pela ferramenta (concluída em 2026-10-09)

Pedido do usuário: montar o template do `webserviceNFSe.xsd` como ficaria no
ERP, carregá-lo na ferramenta e gerar o conversor por ela. Ele não quis que o
código do ERP fosse alterado.

- **Exemplo**, fora do repositório: `<pasta-dev>\exemplos\dps-erp-webservice`.
  - `template/depara.mjs` concentra o DE-PARA, usado pela publicação por
    script e pelo rascunho.
  - `template/gerar-rascunho.mjs` gera `rascunho-webserviceNFSe-erp.json`:
    - entrada: DPS 1.01 + infErp + bloco próprio `infExtra` (chave de
      segurança do município, IE da filial), com o contrato
      `dps-erp-exemplo` salvo;
    - confere, com o `draft.js` do editor, que o mapeamento reconstruído é
      idêntico ao DE-PARA;
    - testa pelo mesmo endpoint do botão "Testar".
  - `erp/`: projeto **.NET Framework 4.8 / C# 12** com o conversor gerado.
    Converte a DPS + `infExtra` sem banco nem API.
- **Editor:**
  - botão **"Gerar conversor C#"** no painel Publicar (`converterRequest`,
    `getConverterCode`), da versão recém-publicada ou da ativa;
  - se o alvo mudar depois de publicar, o editor usa a versão ativa do
    template indicado.
- **Correção no editor e nas camadas:** elemento com conteúdo `xs:choice`
  direto (ramos sem nó de grupo, ex.: `Rps`) agora é tratado como choice,
  também na raiz. Antes, os ramos complexos não escolhidos saíam vazios e
  apareciam como pendentes.
- **Camadas:** `CollectionEntry.SingleObjectAsItem` (antes se perdia).
- **e2e:**
  - passo 10b: gerar o conversor;
  - passo 14: o rascunho do passo 11 é reimportado numa página limpa,
    testado, publicado e tem o conversor gerado, só com dados do repositório;
  - modo `--rascunho <arquivo>` para qualquer rascunho.
  - O rascunho do ERP rodou por esse modo: importado, testado, publicado e
    conversor de 2.552 linhas.

**Gate:** cumprido.

**Revisão independente:** APROVADO COM RESSALVAS. Tratado:
- A1: estado;
- A2: `MappingPaths` alinhado ao editor, com teste;
- A3: choice na raiz;
- A4: testes de choice não repetível, aninhado e na raiz, e fluxo de importação
  reproduzível (passo 14);
- I1: versão do conversor presa ao template publicado.

O rascunho do ERP **não** é versionado no repositório: tem dados reais da nota de
exemplo.

### Fase 18 — Runtime portátil (netstandard2.0) para o ERP (concluída em 2026-10-09)

O usuário definiu que o framework precisa servir o ERP (.NET Framework
4.8) e escolheu as duas integrações. Ver ADR-0022.

- **Alvos:** `build/Portable.props` faz Domain, Xsd, Engine, Contracts,
  Application e Sdk compilarem para `netstandard2.0;net10.0`.
- **Pacotes só no `netstandard2.0`:** PolySharp, DateTimeOnly,
  System.Text.Json, DiagnosticSource e TimeProvider.
- **Shim** `build/Compat/NetStandardCompat.cs`: APIs novas com a mesma
  semântica, via membros de extensão do C# 14.
- **`#if` mínimos:** `GeneratedRegex`, `PeriodicTimer`, busca por `Span` e
  `RandomNumberGenerator.GetBytes`.
- **Formas que valem nos dois alvos:** fatias de array, `TimeSpan / int`,
  `int.Parse(span)`, `IReadOnlySet` → `IReadOnlyCollection` e `Lazy<T>(valor)`.
  O ano-mês como tupla passou a aceitar `ValueTuple<int, int>` ou
  `Tuple<int, int>` explícitos, em vez de qualquer `ITuple` de dois inteiros.
- **`tests/Tradux.NetFramework.Tests` (net48):** roda no .NET Framework
  4.8. Gera e valida o XML, gera o conversor, compila-o em C# 12 com Roslyn
  dentro do 4.8, executa e compara com o motor.
- **Teste de arquitetura:** os 6 projetos do runtime mantêm o alvo
  `netstandard2.0`.
- **Requisito do conversor gerado:** C# 12. O ERP compila em C# 7.3, então os
  conversores vão num projeto de formato SDK com `LangVersion` 12 (Fase 19).
  O cabeçalho do arquivo gerado avisa.
- **Pacotes:** os 6 do runtime saem com `lib/netstandard2.0` e
  `lib/net10.0`. O conector de assinatura continua `net10.0`, porque o ERP
  assina sozinho.

**Gate:** cumprido (ver ROADMAP, Fase 18).

**Revisão independente:** APROVADO COM RESSALVAS. Segurança de XSD e XML de origem
confirmada no .NET Framework 4.8 (configuração toda explícita). Ressalvas tratadas:
- caminho XML → XML e XSD perigoso recusado agora cobertos em net48 (4 testes);
- `RunPollingAsync` recusa intervalo não positivo nos dois alvos;
- shim sem divergências latentes (`Replace` com vazio, `Split` por string vazia,
  `Clamp`, `MaxBy`/`MinBy` com chave nula);
- `Version` 0.6.0 (alvos novos e `IReadOnlySet` → `IReadOnlyCollection` na API);
- timeout de regex no AppDomain do ERP registrado no ADR-0022 (item 5).

### Fase 17 — Regras em C# legível no conversor gerado (concluída em 2026-10-09)

Pedido do usuário: "ao gerar o conversor tem que aplicar as regrinhas também".
Ele escolheu regras em C# legível (ADR-0021, que revê uma alternativa rejeitada
no ADR-0018).

- **Gerador** (`CSharpRuleWriter`):
  - `{P}Regras`: uma regra por destino, chamando os operadores do motor
    (`Ops.*`) e `Truthy` na condição;
  - `{P}Tabelas`: as tabelas DE-PARA;
  - `{P}Conteudo`: a versão embutida.
  - Operador nativo **substituído** no catálogo vira `OperadorCustomizado`.
- **Motor:**
  - `TemplateCodeRules` + `TemplateCompiler.Compile(…, codeRules)`;
  - a mesma rede de segurança de exceções vale para as regras em C#;
  - `CODE_RULE_MISSING`/`CODE_RULE_UNUSED`;
  - `RuleOperatorDefinition.IsBuiltIn`.
- **SDK:** `EmbeddedTemplate` gera pelo mesmo `XmlGenerationService`, sem
  banco. Confere o hash do conteúdo e recusa um catálogo que substitua os
  operadores que o código chama como nativos.
- **Conversor gerado:** local por padrão; o runtime é opcional, só para checar
  a versão publicada.
- **Exemplo:** converte com a API desligada; `--conferir-versao` consulta o
  MongoDB.

**Gate:** cumprido. Equivalência motor × código gerado (mesmo sucesso, XML e
diagnósticos) em 8 templates, sobre cenários e todos os casos estruturais:
- todos os operadores;
- exceções .NET em valor, filtro e ordenação;
- injeção por literal e por nome de tabela;
- operador customizado;
- operador nativo substituído.

**Revisão independente, rodada 1:** REPROVADO. Corrigido, com teste:
- **B1:** injeção pelos comentários XML-doc. Agora quebra de linha e
  controle/separador Unicode são neutralizados.
- **B2:** as regras em C# não tinham a rede de segurança de exceções do motor.
- **B3:** estado do projeto.
- **I1:** operador nativo substituído no catálogo.
- **I2:** o comentário prometia conferência de conteúdo.
- **Menores:** M1 (literais int, long, double e DateOnly) e M2 (nome
  reservado).
- **Aceito:** M3 (compilação dupla no carregamento do `EmbeddedTemplate`).

**Rodada 2:** APROVADO COM RESSALVAS.
- A1 corrigido, com teste: um operador nativo redefinido com `with` herdava a
  marca de nativo. Agora `Register` sempre grava como de terceiros.
- Registrado (I3): o filtro de exceções não cobre `NullReferenceException` e
  similares de operador de terceiros. O motor e o conversor se comportam igual.

Nota de execução: numa rodada completa com o revisor usando a máquina ao mesmo
tempo, o teste de carga de 200/min falhou uma vez. Rodado de novo isolado (4/4)
e com a suíte de integração inteira (86/86), passou.

### Fase 16 — Saneamento: contratos explícitos (concluída em 2026-10-09)

Auto-revisão da Fase 15 a pedido do usuário. Sem funcionalidade nova. Decisão
no ADR-0020, que substitui em parte o ADR-0018 e o ADR-0019.

- **Objeto como item:**
  - opção explícita `CollectionMapping.SingleObjectAsItem`;
  - sem ela, `COLLECTION_SOURCE_NOT_ENUMERABLE` com sugestão;
  - editor: "objeto único: 1 item" no "for each".
- **Escopo raiz:**
  - `FieldReferenceNode.Scope` (`root`) e `SourceDataTree.Root`;
  - `$root.` não compila;
  - o padrão não é serializado, então o hash de templates publicados não
    muda;
  - editor: campos "(raiz)";
  - o conversor gerado põe campos da raiz na entrada tipada (antes ficavam
    de fora).
- **Eventos explícitos:**
  - passam por `XmlGenerationEngine.Generate(…, events)` →
    `XmlGenerationService` → `TraduxRuntime.GenerateXmlAsync/TransformXmlAsync(request, events)`
    → conversor gerado;
  - sem `AsyncLocal`/`Use()`.
- **Suíte estrutural** (`SourceProfile`/`StructuralSuite`):
  - o perfil registra usos por ramo (`BranchUse`) e conteúdo sem dado
    (`RuleExecutor.ContentWithoutData` → `AlwaysPresent`);
  - seleção de ramos com fechamento: ramos forçados primeiro;
  - um caso `ramo:` por ramo (categoria `Choice`);
  - ramo inalcançável → `CHOICE_BRANCH_UNREACHABLE`;
  - ausente é inválido esperado só quando o motor recusa;
  - sai a heurística `SoleBranchContent`.
- **Conector:**
  - sem estado global além da descrição ECDSA padrão;
  - `SigningKey` + `DelegatedRsa`/`DelegatedEcdsa`;
  - `Inspect`/`SignedElements`;
  - chave que não é par do certificado é recusada;
  - Id duplicado (também com atributo customizado) é recusado;
  - Id fora das mensagens.
- **SDK:** `GeneratedTemplateGuard.Version` (era `PinnedVersion`; conversores
  antigos precisam ser regenerados, o que o `tradux-codegen` no build faz).
- **Exemplo:**
  - migrado (`scope: "root"`, `singleObjectAsItem`);
  - a v1 antiga agora falha com erro claro;
  - a v2 foi publicada pelos gates e roda de ponta a ponta (RSA-SHA256,
    ECDSA, RSA-SHA1).

Por que a suíte nova importa: com o template real do exemplo, ela mostrou que
um ramo que contém constante (`CodigoDaObra = ''`, `coalesce(x, '')`) aparece
**sempre**. "Só o ramo X" é impossível nesse caso. O fechamento agora sabe disso
pelo próprio motor, sem estimar.

**Gate:** cumprido.

| Item | Evidência |
|---|---|
| 1, 2 e 4 | `ExplicitContractsTests` (11) e `TraduxRuntimeTests` |
| 3 | Teste de 20 gerações concorrentes com eventos diferentes |
| 5 | 19 testes do conector |
| 6 | JS 31/31 e e2e GATE OK |
| 7 | Exemplo v2 publicado e executado |
| 8 | Ver abaixo |

**Revisão independente, rodada 1:** "gate não cumprido". Corrigido:
- **B1:** fechamento forçava dois ramos em choice único → ramos forçados
  primeiro, e inalcançável reporta.
- **B2:** documentação.
- **I1:** `$root` legado agora não compila.
- **I2:** chave × certificado.
- **I3:** `SignedElements`.
- **I4:** botão "Criar bloco" e arrastar no editor.
- **Menores:** M1, M3, M4 e M5.
- **Aceito:** M2 (primeiro uso), registrado no ADR-0020.

**Rodada 2:** REPROVADO por dois casos legítimos. Corrigido, com teste:
- constante **com condição** (padrão cNaoNIF da DPS) era tratada como sempre
  presente → agora é alternativa dependente do dado;
- constante dentro de **item de coleção** forçava o ramo de fora → agora não
  força.

**Rodada 3:** APROVADO COM RESSALVAS. A ressalva recomendada foi aplicada: o
elemento só é condicional se **todas** as regras dele têm condição. Outras
ressalvas registradas: um ramo condicional exige cenário funcional (ADR-0020);
o ramo inalcançável por campo compartilhado reprova de propósito.

### Fase 15 — Choice, eventos, conversor gerado e assinatura (concluída em 2026-10-09)

> Partes substituídas na Fase 16 (ADR-0020): eventos ambientes, objeto único
> implícito, `$root`, `SoleBranchContent` e algoritmo customizado.

Pedido do usuário: eventos C# antes e depois de cada tag; validação de versão
entre template e código; gerador de conversor C# (tipos, classes); assinatura
genérica da DPS (tag + algoritmo, ou customizado); exemplo com
`webserviceNFSe.xsd` e DPS + `infErp`. Decisões no ADR-0018 e no ADR-0019.

**Motor:**
- `xs:choice`/`xs:all` e grupos aninhados no destino, com grupos
  transparentes no mapeamento;
- diagnósticos `CHOICE_*` e `MAPPING_MISSING_REQUIRED_CHOICE`;
- `GenerationEvents` (`FieldResolving`, `ElementCreated`, `DocumentBuilt`,
  `XmlGenerated`);
- `xs:dateTime`;
- coleção sobre objeto único = 1 item;
- `$root.caminho` dentro de "for each".

**Application:**
- árvore de destino com grupos (`ContentRepeatable`) e `MappingPaths`;
- suíte estrutural com choice, objeto único e `SoleBranchContent`;
- `CSharpConverterGenerator`.

**Superfícies:**
- SDK: `GeneratedTemplateGuard`/`TemplateVersionPolicy` e
  `GenerateConverterCodeAsync`;
- API: `GET …/codegen/csharp` (`template.read`);
- CLI: `tools/Tradux.CodeGen` (`tradux-codegen`, com `--check`).

**Editor:** grupos transparentes e status de choice obrigatório.

**Conector externo** `connectors/Tradux.Connectors.XmlSignature` (`XmlSigner`):
- algoritmos: RSA-SHA1/256/384/512, ECDSA-SHA256 e customizado;
- assina por tag (Id, todas as ocorrências) ou o documento inteiro;
- C14N ou C14N exclusiva;
- três posições para a assinatura;
- `Verify`.

O núcleo nunca referencia o conector, e há teste de arquitetura para isso.

**Exemplo** (fora do repositório): `<pasta-dev>\exemplos\dps-erp-webservice`.
- Publica o DE-PARA DPS 1.01 + `infErp` → `RpsNfse`, com tabelas DE-PARA.
- O conversor é gerado no build.
- Assina a DPS de entrada (`infDPS`), converte com eventos e assina o XML de
  saída.
- Verifica as duas assinaturas e mostra a saída tipada.
- Rodou com RSA-SHA256, ECDSA, RSA-SHA1 e RSA-SHA512.

**Gate:** cumprido.

| Item | Evidência |
|---|---|
| 1 | `Phase15GateTests` e `ConverterCodeGenerationTests.Repeated_choice_layout…`: publicação pelos gates e XML válido |
| 2 | Teste do conversor com eventos antes e depois |
| 3 | Conversor gerado compilado pelo Roslyn: entrada XML e tipada, ramos em lista, saída tipada |
| 4 | Testes de `RequireSameVersion`/`PinGeneratedVersion` e da janela de cache |
| 5 | Endpoint com 200, 401, 403 e 404 de outra organização; CLI `--check` usado no exemplo |
| 6 | 13 testes do conector |
| 7 | `DependencyRuleTests` |
| 8 | Exemplo rodando de ponta a ponta |

**Revisão independente:** o agente `reviewer` deu "gate não cumprido
formalmente". Corrigido:
- **B1:** documentação de estado.
- **B2:** testes de gate (API e Roslyn com choice).
- **I1:** texto do template injetado no código gerado. O cabeçalho agora não
  aceita quebra de linha, e o `Literal` escapa U+2028/2029.
- **I2:** a geração usa sempre a versão do código.
- **I3:** cache do guard estático por runtime.
- **I4:** `Verify` devolve `false` em vez de exceção e documenta que, sem
  certificado, prova só integridade.
- **M1:** mensagem de exceção de evento fora do diagnóstico; cancelamento
  propaga.
- **M5:** namespace vazio.
- **Achado no gate (B2):** ausente/nulo no único campo de um ramo de choice
  não é mais "inválido esperado".

**Limitações aceitas** (ADR-0018):
- **I5:** sem caso estrutural por ramo; um choice único com vários ramos
  mapeados bloqueia a publicação.
- **I6:** um ramo gera no máximo uma ocorrência por pai.
- **M3:** coleção sobre objeto vira 1 item em silêncio.
- **M2:** `FieldResolving` dispara em ramo descartado.
- **M4:** a chave no `--key` do CLI aparece na lista de processos; prefira
  `TRADUX_API_KEY`.
- **M6:** registro global do algoritmo customizado no `CryptoConfig`.

### Fase 14 — Mudanças do XSD de destino e remapeamento (concluída em 2026-10-02)

- Application: `SchemaChangeAnalyzer` (puro):
  - compara pelo modelo canônico, alinhando por caminho lógico; detecta
    elementos movidos e raiz renomeada;
  - reporta mudanças de tipo, valor/bloco, cardinalidade, `nillable`,
    restrições, valor padrão, ordem das tags, modelo de conteúdo e namespace;
  - classifica o impacto como Remap, Map, Review ou Info e dá sugestões;
  - limites: 5.000 mudanças e 500 buscas de sugestão.
- `DraftDesignService.CompareSchemas` e `CompareWithContent`.
- Contracts: `SchemaChangeContracts.cs`.
- API: `POST /v1/design/schema-changes` (`template.edit`) e
  `POST …/templates/{id}/operations/{op}/schema-changes` (`template.read`,
  mesma organização, CORS do editor).
- SDK: `TraduxDesigner.CompareSchemas` e
  `TraduxRuntime.CompareSchemaWithActiveAsync`.
- Editor:
  - compara antes de trocar o XSD de destino (`model/schemaChanges.js`);
  - pendentes com "Mover para <sugestão>", "Mover para o campo
    selecionado" e "Descartar";
  - painel "Mudanças do XSD" (`ui/schemaChangesPanel.js`) e marcas na
    árvore;
  - publicação bloqueada com pendentes; o relatório fica salvo no rascunho.
- Testes: Application +11 (`SchemaChangeTests`), integração +2
  (`Phase14GateTests`), JS +2, e2e passo 13.
- Docs: ADR-0017, DECISIONS, ROADMAP, API README §17, CONSUMERS, CLAUDE.md
  e RAG.
- Revisão independente, rodada 1: REPROVADO. Corrigido:
  - C1: um bloco mapeado que passou a repetir ficava "inativo" e a
    publicação saía sem o mapeamento. Agora o painel lista os mapeamentos
    inativos (definir o "for each" ou descartar) e eles bloqueiam o
    fechamento do relatório e a publicação. Os filhos do for each de um bloco
    que deixou de repetir viram pendentes para conferir.
  - A1: as restrições passaram a ser comparadas de forma estrutural; uma
    enumeração com mais de 20 valores não escapa mais.
  - A2: a Levenshtein ganhou pré-filtro por tamanho e orçamento de 5 milhões
    de células por relatório.
  - A3: elemento movido exige o mesmo tipo e nenhum renome provável no bloco
    original; a sugestão penaliza tipo diferente.
  - A4: `mapping` com itens nulos responde 400.
  - I2: pendentes sem duplicar ao trocar o XSD de novo.
  - I3: validação do relatório salvo no rascunho.
  - I4: teste do SDK e `effectiveDate` do RAG.
- Revisão independente, rodada 2: APROVADO COM RESSALVAS. Corrigido:
  - aviso 1: o mesmo caminho com mapeamentos diferentes vira dois pendentes,
    cada um com id próprio; nada é descartado;
  - aviso 2: um mapeamento que estava ignorado e passaria a ser gerado vira
    pendente para conferir;
  - info 3: bloco movido que deixou de repetir;
  - info 4: enumeração reordenada não conta como mudança;
  - info 7: sugestões refeitas sobre a árvore nova e validação mais estrita
    do relatório salvo.

### Fase 13 — Contrato de entrada versionado com extensões (concluída em 2026-10-01)

- Domain: `SourceExtension`/`SourceExtensionNode`/`SourceValueType`
  (`Domain/SourceContracts`).
- Xsd: `SourceExtensionMerger` (forma combinada; caminho/nome validados;
  limites 2.000 nós/32 níveis); `SourceSchema.WithExtensions`;
  `SourceXmlReader` com extensões (regras próprias + XSD no restante,
  posições preservadas).
- Application: `SourceSchemaContent.Extensions`/`Contract` (hash só com
  extensões); `SourceContractService` (versões imutáveis, dedupe por hash,
  auditoria), `ISourceContractRepository` + `InMemorySourceContractRepository`;
  `DraftDesignService.DescribeContract`; teste com `SourceExtensions`.
- MongoDB: `MongoSourceContractRepository` (`sourceContracts`, índice
  único org+id+versão); snapshot persiste extensões e referência.
- API: `/v1/organizations/{org}/source-contracts` (listar, versões, obter,
  criar; CORS do editor), `/v1/design/source-contract`, extensões na
  publicação. SDK: `TraduxSourceContracts`,
  `TraduxDesigner.DescribeSourceContract`.
- Editor: "Contrato de entrada" (organização, contratos salvos, abrir versão,
  novo a partir de XSD, editar estrutura com + campo/+ bloco/editar/remover,
  salvar nova versão); publicação exige versão salva.
- Testes: Xsd +26, Application +8, integração +4, JS +3, e2e passo 12.
- Revisão independente, rodada 1: REPROVADO. Corrigido:
  1. XML profundo derrubava a API por estouro de pilha → `LimitedXmlReader`
     aplica profundidade e `MaxElements` (100.000) durante a leitura. Isso
     vale também para o caminho sem extensões (achado 12, pré-existente).
  2. Custo quadrático com muitas ocorrências → o XSD base agora é validado
     numa passagem, com `SkippingXmlReader` pulando as extensões.
  3. Extensão sob elemento de conteúdo simples → recusada.
  4. Extensão nula ou sem nome → 400 em todas as rotas.
  5. `sourceContract` da publicação → conferido contra a versão salva da
     organização (mesmo hash).
  6. Namespace nos blocos → conferido.
  7. `xsi:nil`/`xsi:type` em extensão → recusados.
  8. Hash de extensões → calculado sobre a forma canônica.
  11. Comentário → até 1.000 caracteres.

  Os achados 9 e 10 e a corrida do dedupe ficaram como dívida (ver riscos).
- Revisão independente, rodada 2: APROVADO COM RESSALVAS. Corrigido:
  - R1: as extensões não funcionavam com XSD unqualified → cada nó da forma
    guarda o namespace real do elemento.
  - A2: o custo crescia com extensões × pais → extensões agrupadas por pai.
  - A1: a referência ao contrato só era conferida na API → passou para a
    Application e vale também no SDK; na importação de pacote é descartada.
  - O teste de publicação agora exige 422 exato.
- Docs: ADR-0016, DECISIONS, ROADMAP, API README §16, CONSUMERS, CLAUDE.md, RAG.

**Fase 22** (2026-11-16, ADR-0025 — pedido do usuário: "não vai ser mais uma DLL
separada, vai ser apenas um microserviço"; versionamento pelo serviço, não pelo Git):

- Removidos:
  - `src/Tradux.Sdk` (as classes viraram drivers de teste em
    `tests/Shared/Drivers`);
  - `tools/Tradux.CodeGen`, `Application/CodeGeneration`,
    `CodeGenerationEndpoints`;
  - `connectors/` e os testes do conector;
  - `tests/Tradux.NetFramework.Tests`, `build/Portable.props` e os ramos
    `#if NETSTANDARD2_0`;
  - `Application/SourceContracts`, `MongoSourceContractRepository`, as rotas
    `/source-contracts` e `/v1/design/source-contract`;
  - `PackageSnapshotRepository` (runtime offline por pacote);
  - empacotamento NuGet;
  - os testes correspondentes (codegen, equivalência ERP, contratos da Fase 13 e
    Fase 20 pela API, runtime offline).
- API (`ServiceEndpoints`):
  - `GET /v1/templates`;
  - `PUT /v1/templates/{nome}`, com validação de nome;
  - `POST /v1/templates/{nome}/convert`, que devolve XML puro, 422 com
    diagnósticos e `?format=json`;
  - `GET …/versions`, `GET …/versions/{v}` e `POST …/rollback`;
  - a organização vem da credencial;
  - porta `ITemplateCatalog`, implementada no `MongoTemplateSnapshotRepository`;
  - publicar e reativar atualizam na hora o cache da instância. Isso corrigiu uma
    falha intermitente: a mesma instância convertia com a versão antiga logo após
    publicar.
- Editor:
  - tela inicial com os templates do serviço e "Novo template";
  - fluxo 1. XSD de origem → 2. XSD de destino → 3. DE-PARA → 4. Testar (XML de
    exemplo + esperado) → 5. Publicar a próxima versão;
  - histórico com abrir e reativar;
  - `model/openVersion.js` converte a versão publicada de volta para o editor;
  - saíram o modo JSON, os contratos, o conversor e o importar/exportar rascunho;
  - o painel "Trocar o XSD" não fecha mais ao escolher o arquivo;
  - XML esperado em branco não é enviado.
- Implantação: `deploy/Dockerfile.api`, `deploy/Dockerfile.web`,
  `deploy/docker-compose.yml` (MongoDB rs0 + API + editor), `.env.example`,
  `.dockerignore`.
- Docs: ADR-0025 (com status atualizado nos ADRs 0008, 0016, 0018, 0019 e 0021–0024),
  DECISIONS, ROADMAP (Fase 22), `docs/api/README.md` ("Uso do microsserviço"),
  `CLAUDE.md`, `Tradux.Api.http`.

## Em andamento

Fase 22: falta a revisão independente.

## Próximo passo exato

1. Revisão independente da Fase 22 + untangle (agente `reviewer`).
2. Construir e subir o compose num ambiente com Docker (`deploy/`). Esta máquina não
   tem Docker, então as imagens **não foram construídas**.
3. O e2e grava templates `pedido-e2e-*` e `nota-erp-e2e-*` na base de desenvolvimento
   (`<pasta-dev>/mongo-data`, org-a). Perguntar ao usuário se quer limpá-los.
4. O exemplo do ERP (`<pasta-dev>/exemplos/dps-erp-webservice`) usava o
   conversor gerado, que saiu. O template `webservice-nfse-erp` publicado continua
   convertendo pelo serviço. A operação dele não foi conferida: se não for `default`,
   chame com `?operation=<operação>`.

## Decisões tomadas

Ver `docs/DECISIONS.md` para o log completo. Resumo Fase 0: requisito de
origem é fonte normativa única; trabalho é sequencial por fase com gate
obrigatório; estrutura de agentes segue exatamente as seções 4–5; documentação
em português. Resumo Fase 1: motor compila templates para delegates (não
interpreta) — decisão do usuário, C++ com interop rejeitado explicitamente;
.NET 10 LTS adotado como alvo (confiança média); versionamento
`nome@MAJOR.MINOR` + hash; MongoDB só por adaptadores, snapshot denormalizado;
cache local ao processo com invalidação via Change Streams; camadas como
overrides por identificador lógico estável; arquitetura geral formalizada em
ADR-0007 (portas em `Application`, não em `Domain`). Resumo Fase 3: modelo
de schema vira árvore de partícula completa (sequence/choice/all, aninhada);
projeção plana da Fase 2 é `null` quando não aplicável, nunca falha
silenciosa; `include`/`import` só resolvem dentro de um diretório/pacote
permitido, nunca remoto; pacote ZIP é resolvido inteiramente em memória;
hash de conteúdo garante import determinístico. Resumo Fase 4: mapeamento
vira árvore de regra (RuleNode) — dado puro, nunca código; catálogo de
operadores é data-driven e extensível via `Register`, sem alterar o
núcleo; condição de mapeamento (grupo de regras) decide SE um campo se
aplica; resolução de `xs:choice`/`xs:all` em tempo de geração fica fora
do escopo — mapear um choice falha alto e claro, nunca silenciosamente.
Resumo Fase 5 (ADR-0008): um único caso de uso de geração para API e SDK;
SDK recebe o repositório por injeção e nunca referencia infraestrutura;
snapshot autocontido inclui os XSDs; versão de snapshot = sequência por
chave + hash; JSON normalizado (número→decimal, nomes exatos); HTTP
200/404/400; testes de integração com MongoDB real sem Docker.

## Dúvidas abertas

1. ~~Versão exata do .NET 10~~ — **Resolvida em 2026-09-28**: confirmado via
   `dotnet --list-sdks` que o ambiente de desenvolvimento já tem
   `10.0.400` instalado (junto com `8.0.424` e `9.0.317`), validando a
   suposição do ADR-0002. Usar o SDK `10.0.400` (ou mais recente da mesma
   major) a partir da Fase 2. Continua valendo checar a página oficial de
   releases antes de fixar TFM/patch definitivo no `global.json`.
2. ~~SDK com referência opcional a `Infrastructure.MongoDb`~~ —
   **Resolvida na Fase 5 (ADR-0008)**: o SDK nunca referencia
   infraestrutura; o host que embute o SDK injeta o repositório (pacote
   `Tradux.Infrastructure.MongoDb` separado). Protegido por
   `ArchitectureTests`. Reversível se o dono do produto preferir um pacote
   "SDK + MongoDB" de conveniência — seria um pacote novo, sem mudar o SDK.
3. **Cardinalidade Fornecedor×Município×Template** (levantada pelo agente
   `domain-xsd`): assumido 1 Template → 1 Provider + 0..1 Municipality
   (layout nacional multi-município vira camada, não Template novo) em
   `docs/domain/DOMAIN_MODEL.md` §7. A confirmar se essa é a intenção real.
4. **Retenção de `ValidationReport`** (idem): modelado como registro pontual
   por execução, sem política de retenção definida — a decidir na Fase 9
   (observabilidade/diagnóstico).
5. **Granularidade de `FallbackPolicy`** (idem): assumida por Template (não
   por operação isolada) — a confirmar se alguma operação (ex.: cancelamento)
   precisar de uma política de fallback diferente da de emissão.
6. **Permissões inferidas para 3 fachadas sem permissão explícita na seção
   20** (levantada pelo agente `api-sdk`): "importar contrato" →
   `template.create`; "validar rascunho" → `template.edit`; "rollback" →
   `template.publish`. A confirmar (ou ajustar) quando o agente
   `security-observability` implementar autorização na Fase 9.

Nenhuma dessas dúvidas bloqueia o gate da Fase 1 (nenhuma delas expõe
infraestrutura em domínio/motor nem deixa um limite crítico indefinido) — são
decisões de detalhe a confirmar antes das fases que dependem delas.

## Riscos e bloqueios

- Toda rota `/v1` exige credencial com permissão (Fase 9). A credencial de
  desenvolvimento (`appsettings.Development.json`, hash de
  `tradux_dev-admin-org-a`) nunca deve ir para produção.
- Testes de integração dependem de baixar o MongoDB uma vez
  (`EphemeralMongo`); ambiente sem internet na 1ª execução → os testes
  de `Tradux.Api.IntegrationTests` (os que usam o fixture) falham na inicialização (os demais não são afetados).
- Risco a observar: o requisito de origem é extenso (30 seções); sessões
  futuras devem reler apenas as seções relevantes à fase ativa (ver mapa
  em `docs/REQUIREMENTS.md`), não o documento inteiro.

## Arquivos alterados nesta sessão

Sessão da Fase 5 (tudo no commit "Fase 5"): projetos novos
`src/Tradux.{Application,Infrastructure.MongoDb,Api}`,
`tests/Tradux.{Application.Tests,Api.IntegrationTests,ArchitectureTests}`,
`tests/Shared/SnapshotFixtures.cs`; alterados `Domain` (`RuleNode`,
`LiteralValueJsonConverter`, `SourceDataTree`), `Xsd` (`ImportInMemory`),
`Engine` (`RuleExecutor`), `Contracts` (todos os arquivos), `Sdk`
(`TraduxRuntime` novo, `TraduxClient`), testes de Engine (sintaxe
`OperatorNode.Of`), Domain.Tests e Xsd.Tests (novos testes),
`Directory.Build.props`, `.gitignore`, `Tradux.slnx`; docs listados
em "Concluído" → Fase 5.

## Comandos de build e teste

- `dotnet build` — build limpo, 0 avisos, 0 erros (conferido com `--no-incremental` — o build incremental já escondeu um aviso).
- `dotnet test` — ver "Último resultado de testes" (integração sobe um `mongod` real e
  roda a carga curta da Fase 11; ~3 min).
- Não há mais pacotes (ADR-0025).
- Ver também `CLAUDE.md` → "Comandos de build/teste".

## Último resultado de testes

2026-11-19, depois da renomeação para Tradux (ADR-0026). `dotnet build`: 0 avisos, 0 erros.
`dotnet test`: 554/554. A 1ª execução teve 1 falha (`Composite_fixture_zip_gives_the_same_result_as_the_loose_files`):
o ZIP `docs/schemas/exemplo-composto-pacote.zip` ainda trazia os XSDs com o namespace
antigo. O ZIP foi regenerado a partir dos XSDs soltos e `Tradux.Xsd.Tests` passou 142/142.
Depois disso, a credencial de dev não autenticava. O texto da chave tinha mudado, mas o
hash em `appsettings.Development.json` ainda era o de `nfse_dev-admin-org-a`. O hash foi
corrigido para o de `tradux_dev-admin-org-a`.
A base de dev foi copiada de `nfse-mapping` para `tradux-mapping`:
- copiados: 139 snapshots, 63 `sourceContracts` e 706 eventos de auditoria;
- não copiada: a credencial `nfse_`;
- o banco antigo ficou intacto.
e2e `golden-path.mjs`: GATE OK em 2026-11-19, contra a API e o editor Tradux locais.
Depois de neutralizar a documentação para o repositório público: build 0 avisos, `dotnet test` 554/554.

Anterior: 2026-11-18, untangle onda 4 (final).
`dotnet build --no-incremental`: 0 avisos, 0 erros. `dotnet test`: 554/554, 0 falhas.

| Projeto | Resultado |
|---|---|
| `Tradux.Domain.Tests` | 24/24 |
| `Tradux.Xsd.Tests` | 142/142 |
| `Tradux.Application.Tests` | 227/227 |
| `Tradux.Engine.Tests` | 63/63 |
| `Tradux.Knowledge.Tests` | 7/7 |
| `Tradux.Api.IntegrationTests` | 76/76 |
| `Tradux.ArchitectureTests` | 14/14 |
| `Tradux.Web.Tests` | 1/1 (`node --test tests/Tradux.Web.Tests/model.test.mjs`: 38/38) |

- Onda 4 (ADR-0025 §8): um só leitor de entrada JSON e XML (`SourceInputs.Json`/`Tree`/`Seed`,
  sempre com os limites); os limites de `Tradux:Generation:Limits` chegam aos gates de
  publicação e à regressão de camadas (antes usavam o padrão fixo); regressão de
  cenários lista o cenário não comparado (`ScenarioRegression.Problem`: ativa que não
  compila ou entrada ilegível, só tipo/limite/posição) em vez de omiti-lo; auditoria do
  teste/qualidade de rascunho dentro de `DraftDesignService.TestAsync/QualityAsync`
  (sai `DraftTestAudit`); `DuplicateKey.Is/RetryAsync` únicos no MongoDB; registro DI
  só por interface para os adaptadores Mongo (exceto o repositório de snapshots);
  `MapTraduxGenerationEndpoints`; `LayerOverrides` foi para `tests/Shared`; editor usa
  `report.passed` da API e `readFileAsBase64` mora em `model/files.js`; montador único
  de `SchemaUpload` nos testes (`SnapshotFixtures.Upload/UploadFile`).
- Testes novos: limites configurados nos gates (`GenerationLimitsTests`), cenário
  ilegível e ativa que não compila na regressão (`QualityGateTests`), auditoria do
  rascunho no serviço (`DraftDesignServiceTests`).
- e2e no navegador (`golden-path.mjs`): GATE OK em 2026-11-18 depois da onda 4, contra a
  API e o editor locais com o MongoDB de desenvolvimento. Docker: não construído.
- Observado uma vez (onda 1): `Phase9GateTests.Normal_logs_traces_and_metrics_never_carry_fiscal_values`
  falhou numa execução da suíte inteira sem mensagem capturada; passou em todas as
  ~15 execuções seguintes.

## Dívida técnica aceita

Fase 22:
- O motor ainda tem o código de extensões de origem (ADR-0016), só para ler snapshots
  antigos.
- Editor só abre templates da operação `default`; os publicados antes da Fase 22 (operação
  `emissao`) aparecem na lista sem "abrir", mas convertem com `?operation=emissao`.
- Fora do escopo do untangle (mudariam comportamento ou são recurso novo): gestão de
  credenciais por HTTP; captura diagnóstica no `/convert`; regra de nome do template em
  todas as rotas; extensões de origem perdidas ao reabrir versão antiga no editor;
  `DraftTestRequest.Data` obrigatório embora o editor não o envie; teste do
  `MongoTemplateUpdateRepository.TransitionAsync` condicional contra Mongo real.
- A busca de conhecimento (RAG) não tem corpus configurado nos contêineres: fica
  indisponível lá.
- O agente `.claude/agents/api-sdk.md` ainda leva "SDK" no nome e na descrição.

Ver itens por fase abaixo (Fase 8: "Riscos aceitos").

Docs: `docs/architecture/`, `docs/domain/`, `docs/api/`,
`docs/schemas/` e `docs/operations/` foram preenchidos na Fase 1. `docs/rag/`
foi entregue na Fase 10 (registro de fontes, chunking, avaliações, manifesto
do índice) e `docs/runbooks/` tem os runbooks operacionais.

Simplificações deliberadas ainda válidas das Fases 2–3: atributos sem valor
fixo (como `id` na fixture simples) não são mapeáveis ainda; `xs:any` não
tem fixture de teste (nenhum uso real apareceu até agora).
(`VersionUsed`/`VersionsAttempted` chegaram na Fase 5.)

Riscos aceitos da Fase 13 (ADR-0016):
- Elementos de extensão ficam no namespace do pai; a ordem deles dentro do
  pai não é imposta; tipos de extensão só texto/número/data (sem
  pattern/enumeração); camadas (base) ainda não carregam extensões — o
  contrato com extensões entra pela publicação de conteúdo (editor/API).
- Dívida da revisão da Fase 14:
  - **Discriminador inválido no corpo.** Um nó de regra com discriminador
    desconhecido ou ausente (`$type`) no JSON ainda dá 500 na desserialização do minimal API. Já
    era assim em `/v1/design/quality` e `/test`.
  - **Caminhos dentro de grupos.** No relatório, o caminho de um nó dentro de
    grupo choice/all omite o segmento do grupo. Por isso a marca na árvore não
    aparece para esses nós, que não são mapeáveis.
  - **e2e do caso C1.** O passo 13 do e2e cobre renome e campo novo; o caso
    C1 (bloco que passou a repetir) é coberto por testes JS e de unidade.
  - **Falha na comparação.** Se a comparação falhar (ex.: o XSD anterior do
    rascunho não importa mais), não há opção "trocar sem comparar".
  - **Limites numéricos.** São comparados como texto: `10` → `10.0` aparece
    como mudança, e qualquer mudança de limite é tratada como "mais rígida".
- Dívida da revisão da Fase 13:
  - A3: na conversão, um elemento de outro namespace aceito por `xs:any`
    pode ocupar o campo de mesmo nome local, sem a regra de tipo. Isso já
    acontecia com campos do XSD base.
  - I2: XML com DTD é recusado sem posição (0:0).
  - I3: `MaxElements` (100.000) não é configurável pelas opções da aplicação.
  - dois salvamentos idênticos simultâneos podem criar duas versões iguais
    (o dedupe não é atômico, mas a numeração é);
  - `ListLatestAsync` (MongoDB) carrega os arquivos de todas as versões para
    montar o resumo (falta projeção e `$group`);
  - `SourceValueType` mudou de `Tradux.Xsd` para
    `Tradux.Domain.SourceContracts`, quebrando quem já consome o pacote
    (registrado no ADR-0016).
- Contrato só é compartilhado dentro da organização; não há faixa de versão
  (`1.x`) — o template fixa uma versão exata.

Riscos aceitos da Fase 12 (ADR-0015):
- Namespaces da origem reduzidos a nomes locais; atributos sempre texto;
  conteúdo misto ignorado; tipos recursivos além do 2º nível não aparecem na
  árvore do editor (o leitor ainda converte).
- Mensagens do validador de XML (idioma do runtime) podem conter valores do
  XML enviado — vão só para quem enviou; gates, logs e auditoria usam só
  linha/coluna/código.
- Suíte estrutural continua gerando dados sintéticos a partir do mapeamento
  (não do XSD de origem); XML de cenário entra só como semente.
- Revisão da Fase 12 (avisos aceitos): double/float fora da faixa de
  `decimal` arredondam; `xs:token` sem colapso de espaços; mesmo nome em
  ramos de `choice` vira lista; nomes com ponto não endereçáveis; nova base
  sem XSD de origem só é barrada se houver cenário em XML; estouro de limite
  na entrada XML não registra a métrica de geração (o 400 é o mesmo);
  expansão exponencial de tipos no importador de destino (anterior) sem teto.

Riscos aceitos da Fase 11 (ADR-0014):
- Latência medida em processo (sem rede entre máquinas, TLS, gateway) e com
  o template de exemplo; template real deve ser medido de novo.
- Sem timeout por geração (execução em memória, linear e limitada pela
  entrada); sem limitação de taxa na API (gateway).
- Com o MongoDB fora, credencial já validada vale até a carência (10 min):
  revogação feita em outra instância pouco antes da queda atrasa até esse
  tempo; cache de snapshots cresce com as versões usadas (sem despejo).
- Saturação medida com log de console em Development; produção deve usar
  OTLP e console em Warning.

Riscos aceitos da Fase 10 (ADR-0013):
- Busca lexical (BM25), não semântica: perguntas com vocabulário diferente
  do documento recuperam pior — a avaliação documenta o nível atual.
- Pacote com camadas só na organização de origem; importação exige as
  mesmas versões de camada (ambientes divergentes = conflito explícito).
- Pacote = versão ativa (ou uma versão); histórico completo é backup do MongoDB.
- Fontes de organização no RAG (casos de teste publicados) têm mecanismo
  (escopo por tenant) mas não são alimentadas automaticamente pela API.
- Importação recusada depois de gravar camadas próprias deixa essas camadas
  no destino (imutáveis, sem vínculo, reaproveitadas pela próxima importação);
  o hash da camada é integridade, não autenticidade — a origem é confiada por
  quem tem `template.import` + `template.publish` + `template.create` (revisão A2).
- Reindexação do RAG = reiniciar a instância (sem endpoint de reindexação);
  falha de indexação responde 503 e tenta de novo (revisão A6).
- `TraduxPackages` (SDK) importa só com os gates obrigatórios; gates
  adicionais do cliente ficam para a publicação normal (revisão A7).

Riscos aceitos da Fase 9 (ADR-0012):
- Gestão de credenciais só pelo SDK `TraduxAccess`/configuração (não
  há permissão de gestão de credencial na seção 20); JWT/OIDC não implementado.
- Sem limitação de taxa de autenticação (decidido na Fase 11: fica no gateway, ADR-0014); limite de
  registros da captura é por processo; proposta pode ficar em `Applying`
  se o processo cair no meio da aprovação.
- Credencial de desenvolvimento (`tradux_dev-admin-org-a`) é pública: nunca
  rodar produção com `ASPNETCORE_ENVIRONMENT=Development`.
- Revogação leva até 30 s nas outras instâncias (cache de credencial); na
  Fase 11, até 30 s + carência (10 min) numa instância sem acesso ao MongoDB.
- `PatternSafetyChecker` é heurístico (não pega alternativas sobrepostas).
- Captura diagnóstica mascara por nome de campo: valores em campos de nome
  não sensível ficam legíveis a quem tem `diagnostic.capture` (cifrados em
  repouso, com expiração).
- SDK em processo (`TraduxRuntime`) não faz captura diagnóstica nem
  autorização — quem hospeda o SDK controla o acesso.

Riscos aceitos da Fase 8 (ADR-0011):
- Perfil de origem é inferido (não há contrato de origem tipado): valor
  típico de campo usado só em expressão pode reprovar regra legítima; o
  relatório aponta caso e XPath.
- Cobertura de `xs:choice`/`xs:all` fica como "não coberto" até o motor
  mapear esse conteúdo. Pattern fora do subconjunto: o campo usa o valor do
  cenário funcional do autor; sem ele, se alimenta obrigatório, a suíte
  reprova (`SYNTHETIC_VALUE_UNAVAILABLE`) — nunca aprova por omissão.
- Filtro/condição sobre literal específico pode reprovar falso; remédio é o
  cenário do autor com o valor (sem dispensa de caso).
- Publicação direta numa chave vinculada a camadas continua possível (agora
  com gates e auditoria); a próxima atualização registra
  `ACTIVE_WITHOUT_LAYERS`.

Simplificações deliberadas da Fase 6 (ADR-0009) — histórico:
- (Superado: a Fase 7 deu publicação ao editor e a Fase 9 autenticação e
  autorização em toda rota `/v1`, inclusive `/v1/design/*`.) Na Fase 6 o
  editor não publicava e a API não autenticava.
- Árvore de origem inferida da amostra (sem contrato de origem tipado
  persistido); campo ausente da amostra não é oferecido para escolha.
- Rascunho só no navegador (localStorage + arquivo exportado).
- Teste de navegador (`e2e/golden-path.mjs`) é manual (precisa de API,
  editor e MongoDB rodando); o `dotnet test` cobre só a lógica JS pura.
- `xs:choice`/`xs:all` e grupos sem elemento aparecem como não
  mapeáveis; atributos de destino sem valor fixo ainda não são mapeáveis.
- Acessibilidade: controles rotulados, árvores com papéis ARIA e mapeamento
  por clique como alternativa ao arraste; sem navegação por setas dentro
  das árvores (Tab percorre os nós; o foco é preservado a cada ação).
- Construtor de condições oferece os 6 operadores de comparação/existência;
  operadores booleanos customizados do catálogo não aparecem nele.
- Comparação de XML aponta só a primeira diferença (sem diff completo).

Simplificações deliberadas da Fase 5:
- Publicação só pelo SDK/`Application` (`SnapshotPublisher`), sem
  camadas, sem gate de testes estruturais/funcionais, sem rollback — tudo
  isso é Fase 7. O publicador já recusa conteúdo que não compila ou tem
  diagnóstico crítico de compilação.
- `versionsAttempted` tem no máximo 1 item (ativa ou fixada); fallback
  entre candidatas na Fase 7. `contexto` de entrada (§3 do doc de API)
  ainda não suportado.
- Diagnóstico não tem `origem`/`regraResponsavel` (§4 do doc de API) —
  Fase 9 (observabilidade).
- Limite padrão de pacote XSD (20 MB) > limite de documento MongoDB
  (16 MB): conjunto enorme compila e falha ao gravar (ADR-0008).
- Snapshot ainda não carrega `providerId`/`municipalityId`/`baseTemplateId`
  nem o manifesto de versões de camadas do ADR-0003/0004 — entram com o
  modelo de camadas (Fase 7).
- Cache de versões fixadas não tem limite de tamanho (versões são
  finitas por chave); medir na Fase 11.
- Itens de revisão não aplicados (detalhe em "Resultado da revisão
  independente da Fase 5"): sem cache negativo para chave inexistente
  (Fase 9, junto com autenticação/limite de taxa); recarga do cache sob
  lock com `CancellationToken.None`; Change Stream sem resume token;
  colisão silenciosa de nomes XSD normalizados; atraso de reconexão fixo
  no SDK.
- Cada chamada do SDK com objeto C# serializa o objeto com o
  `System.Text.Json` (equivalência por construção, ADR-0008 §5) — medir o
  custo na Fase 11 junto com o custo de reflection por item de coleção.
- Elementos de destino `xs:dateTime` (e outros tipos além de string/
  decimal/date/gYearMonth) ainda não são suportados na geração — a
  importação falha alto (`NotSupportedException`). Datas/horas com fuso já
  funcionam em regras (comparação), não como valor de elemento emitido.
- Filtro do Change Stream não cobre desativação sem novo ativo nem
  deleção (inexistentes hoje) — tratar junto com rollback/arquivamento na
  Fase 7.

Simplificações deliberadas da Fase 4 (escopo documentado, não dívida não
planejada):
- **Resolução de `xs:choice`/`xs:all` em tempo de geração continua fora de
  escopo.** Um elemento com esse conteúdo, se obrigatório e sem
  mapeamento, produz o diagnóstico padrão de "obrigatório sem mapeamento";
  se alguém tentar mapeá-lo mesmo assim, a compilação lança
  `NotSupportedException` — nunca gera XML incorreto silenciosamente. Isso
  significa que `exemplo-composto.xsd` (que tem `tomador` como choice)
  ainda não consegue gerar um documento completo válido — só
  `exemplo-simples.xsd` e `exemplo-colecao.xsd` (sem choice) provam o gate
  de geração desta fase. Nenhuma decisão de design de como resolver choice
  foi tomada ainda (precisa de ADR próprio quando a Fase 5+ ou uma
  necessidade real exigir).
- **`OrderByKey` de `CollectionMapping` existe no modelo mas tem cobertura
  de teste mínima** (a seção 14 já descreve ordenação como "opcional").
- **Cardinalidade mínima de coleção (`COLLECTION_TOO_FEW_ITEMS`) não tem
  teste dedicado** — nenhum fixture atual declara uma coleção com
  `minOccurs >= 1`; a lógica existe e é simples (comparação de contagem),
  mas não foi exercitada por um cenário real ainda.
- Interfaces `ITemplateCompiler`/`IRuleExecutor` mencionadas em
  `docs/architecture/COMPONENTS.md` (Fase 1) não foram criadas — há apenas
  uma implementação de cada (`TemplateCompiler`/`RuleExecutor` como
  classes estáticas), então uma interface não agregaria valor ainda; a
  criar se/quando uma segunda implementação for necessária.
- **`SourceDataTree.FromObject` roda reflection uma vez por item de
  coleção, por geração** (`RuleExecutor.ExecuteCollection`) — apontado pela
  revisão da Fase 4. Não viola ADR-0001 (nenhuma regra é reinterpretada,
  só o binding do objeto de origem para a árvore de dados usa reflection,
  como já acontecia desde a Fase 2), mas o custo cresce com o tamanho da
  coleção. Registrar como item a medir explicitamente nos benchmarks da
  Fase 11 (`.claude/skills/performance-review/SKILL.md`); se relevante,
  a Fase 11 pode trocar reflection por um binder compilado por tipo,
  reaproveitando o mesmo padrão de `System.Linq.Expressions` do resto do
  motor.

## Resultado da revisão independente da Fase 21

- **1ª rodada: REPROVADO.** Uma sonda descartável achou 6 cenários em que o motor gerava e o
  código gerado lançava exceção ou gerava outro XML. Todos foram corrigidos:
  - **B1:** `xs:boolean` da origem saía `True`. Agora sai `true`/`false`, via `TextoLogico`.
  - **B2:** invólucro de lista opcional vazio saía `<Lista/>`. Agora não sai (`NuloSeVazio`).
  - **B3:** opcional com valor padrão ou `xsi:nil` sumia. `AlwaysContent` passou a seguir o
    `HasContent` do motor.
  - **I1:** `int`/`double`/`boolean` da saída mudavam o texto (`007` virava `7`) e `10.5`
    era truncado. Agora saem como `string`.
  - **I2:** a mudança de saída para templates já publicados ficou registrada no ADR-0024.
  - **I3:** entraram a fixture `tipos` e o teste do motor para o opcional sem conteúdo.
  - **I4:** o status no ROADMAP foi corrigido.
  - **M1–M7** tratados: avaliação única do `XxxSpecified`, `$index` sem aviso, nomes
    reservados, `DateTime` documentado, atributo com default/fixed, `?inputNamespaces=` com 400
    e `CLAUDE.md`.
- **2ª rodada: APROVADO COM RESSALVAS** (só menores). A sonda passou em 9 de 10 casos; o
  restante (booleano `1` lido de `bool`) é diferença aceita no ADR-0024.
  - A ressalva foi incluída no gate.
  - A colisão do nome `…Informado` com variáveis locais foi corrigida.
  - O teste do motor fica em `Application.Tests` (atende o gate).

## Resultado da revisão independente da Fase 20

**Rodada 1: REPROVADO.**
- **B1:** o código gerado ignorava o `XxxSpecified` das classes xsd.exe do
  ERP. Campo opcional ausente era lido como 0 e saía um valor fiscal errado,
  sem erro. Reproduzido com a DPS do exemplo sem `ValorLiquidoNFSe`: saía
  0.00, o motor dá 167.20.
- **Importantes:**
  - I1: `xsi:nil` em choice e em grupo opcional;
  - I2: path traversal na extração do ZIP pela CLI;
  - I3: nomes de tipo iguais na origem e no destino;
  - I4: fuso do `DateTime`;
  - I5: sem prova em CI no net48;
  - I6: documentação declarava a fase concluída antes da revisão;
  - I7: limites não documentados.
- **Menores:** M1 a M8.

**Correções:**
- B1:
  - leitura pelo dono, `ValorOrigem.X(dono, "Campo", o => o.Campo)`, que
    respeita o `Specified`;
  - modelo de entrada gerado tipado como o xsd.exe (decimal +
    `XxxSpecified`), para a suíte cobrir o caso.
- I1: `IsNullable` só fora de choice e de grupo opcional.
- I2: a base da pasta de saída termina com separador.
- I3: alias `Destino.`.
- I4: `zzz` no `DateTime` local.
- I5: teste net48 `Erp_converter_compiles_and_matches_the_engine_on_netframework`.
  Ele achou mais um bug: o namespace `{ns}.Entrada` colidia com a raiz
  `Entrada`; agora é `{ns}.ModeloDeEntrada`.
- I7: limites documentados no ADR-0023.
- M1 a M6 corrigidos. M7 e M8 aceitos.

**Rodada 2: APROVADO COM RESSALVAS** (só menores).
- N1, `Specified` com nome reservado do C# (`@class`): corrigido.
- N2, `DateTime` sem fuso à meia-noite: documentado no ADR-0023.
- N3, colisão com tipos do sistema fora do XSD de origem: corrigido. O
  helper usa sempre o alias `Destino.`.

## Resultado da revisão independente da Fase 0

Executada por agente `reviewer` isolado em 2026-09-28. Veredito original:
**GATE NÃO CUMPRIDO** (ainda), por dois achados:

1. **[ALTO]** Nenhum commit git existia; `PROJECT_STATE.md` já citava "o
   primeiro commit" como se existisse. Corrigido: commit inicial criado
   logo após a revisão, cobrindo toda a estrutura da Fase 0.
2. **[MÉDIO]** Contradição interna: a lista "Concluído" afirmava que a
   revisão já tinha sido executada e confirmado o gate, enquanto esta seção
   ainda dizia "a preencher". Corrigido: esta seção agora reflete o
   resultado real, e "Concluído"/"Em andamento" foram reescritos para não
   antecipar um resultado que ainda não existia no momento em que foram
   escritos.

Achados adicionais, não bloqueantes, também corrigidos:

3. **[BAIXO]** Contagem incorreta "10 agentes especialistas" — o requisito
   de origem define 9 especialistas (seções 5.2–5.10) + 1 revisor (5.11).
   Corrigido neste arquivo e pendente de correção em `docs/DECISIONS.md`
   (ver próximo passo).
4. **[RECOMENDADO]** `CLAUDE.md` não tinha o passo de "conferir o estado
   real do repositório e corrigir divergências" da seção 6 do requisito de
   origem — lacuna que permitiu o achado 1 passar despercebido até a
   revisão. Corrigido: passo adicionado a `CLAUDE.md`.

Pontos fortes confirmados pela revisão: estrutura `.claude/agents/` (10/10),
`.claude/skills/` (8/8) e `.claude/rules/` (4/4) correspondem exatamente à
seção 4 do requisito de origem; limites obrigatórios do núcleo (seção 2)
declarados de forma consistente em todos os documentos relevantes; nenhum
esqueleto de `docs/` finge completude.

**Gate considerado cumprido após as correções acima** (commit criado,
contradição removida, contagem corrigida, `CLAUDE.md` atualizado).

## Resultado da revisão independente da Fase 1

Executada por agente `reviewer` isolado em 2026-09-28, sobre as 6 entregas do
backlog produzidas em paralelo (diagramas, ADRs, modelo de domínio, contratos
de API/SDK, threat model, fixtures de XSD) mais o ADR-0007 e a consolidação
de dúvidas feitos pelo coordenador. O revisor compilou de fato as fixtures de
XSD com `XmlSchemaSet` para verificar a alegação de "compilação bem-sucedida"
(confirmado) e conferiu `git status` para confirmar que nada da Fase 1 tinha
sido commitado prematuramente. Veredito original: **GATE NÃO CUMPRIDO**
(pendente de correções simples, não estruturais), por dois achados
bloqueantes:

1. **[CRÍTICO]** Contradição em `docs/domain/DOMAIN_MODEL.md`: §1 listava
   "camada de produto" como entidade de plataforma compartilhada, sem
   organização, enquanto §3.11 e o diagrama de classes já a modelavam com
   `OwnerOrganizationId` e como protegida — justamente o tipo de ambiguidade
   que `docs/operations/THREAT_MODEL.md` (TM-01) trata como crítica para
   isolamento multiempresa. Corrigido: §1 agora declara explicitamente que
   camada de produto É tenant-scoped (por `OwnerOrganizationId`), distinta
   das entidades de plataforma verdadeiramente sem dono (fornecedor,
   município, operação, schema de destino).
2. **[ALTO]** `handoffs/CURRENT.md` estava desatualizado (ainda mandava
   "iniciar a Fase 1"), violando o passo 8 de `CLAUDE.md` e contradizendo
   `docs/PROJECT_STATE.md`. Corrigido: handoff reescrito refletindo o estado
   real e a tarefa seguinte real.

Achados adicionais, não bloqueantes, também corrigidos:

3. **[MÉDIO]** `ADR-0004` ainda descrevia a localização das portas de
   repositório como pergunta em aberto, apesar de `ADR-0007` já ter
   resolvido isso. Corrigido: nota cruzada adicionada em ADR-0004.
4. **[MÉDIO]** `docs/operations/THREAT_MODEL.md` (TM-20) recomendava que
   nenhum campo de override aceitasse blob de arquivo livre sem schema, mas
   `ADR-0006` não declarava isso. Corrigido: declaração adicionada às
   consequências de ADR-0006.
5. **[INFORMATIVO]** Confirmado `dotnet 10.0.400` já instalado no ambiente,
   reduzindo a incerteza da dúvida aberta sobre versão .NET (ver "Dúvidas
   abertas").

Pontos fortes confirmados pela revisão: nenhuma dependência de
infraestrutura em `Domain`/`Engine` em nenhum documento; nenhum vazamento do
escopo do núcleo (sem REST/SOAP, autenticação de fornecedor, assinatura,
criptografia ou certificado); ADR-0007 não contradiz os ADRs 0001–0006;
fixtures de XSD claramente rotuladas como sintéticas e comprovadamente
compiláveis; as 6 dúvidas abertas relevantes já estavam consolidadas em
`docs/PROJECT_STATE.md` antes da revisão; "Operação" tratada de forma
consistente como catálogo aberto em todos os documentos.

**Gate considerado cumprido após as correções acima.**

## Resultado da revisão independente da Fase 2

Executada por agente `reviewer` isolado em 2026-09-28. O revisor não confiou
nas alegações do coordenador: rodou `dotnet build`, `dotnet test` e
`git status` ele mesmo, e leu todo o código-fonte de `Domain`, `Xsd`,
`Engine`, `Contracts`, `Sdk` e todos os testes. Confirmou de forma
independente: build limpo (0 avisos), 21/21 testes passando (número antes
das melhorias pós-revisão), nada da Fase 2 commitado prematuramente,
`.gitignore` funcionando (nenhum `bin/`/`obj/` seria staged).

Veredito: **GATE CUMPRIDO**, sem nenhum achado bloqueante. Achados não
bloqueantes, todos corrigidos mesmo assim antes do commit:

1. **[BAIXO]** Documentação referenciava `Tradux.sln`, mas
   `dotnet new sln` gerou `Tradux.slnx` (formato de solução XML novo
   do SDK .NET) — build/teste funcionavam mesmo assim (o SDK descobre
   `.slnx` automaticamente), mas confundiria uma sessão futura procurando
   um `.sln` inexistente. Corrigido: 3 referências em `CLAUDE.md`/
   `docs/PROJECT_STATE.md` atualizadas para `.slnx`.
2. **[AVISO]** `XsdValidator` só repassava a mensagem crua da exceção de
   validação, sem `DestinationXPath`/`Suggestion` (ao contrário dos
   diagnósticos de `TemplateCompiler`/`RuleExecutor`), e nenhum teste
   exercitava esse caminho — risco de diagnóstico pobre justamente no
   estágio que vai pegar violações de `pattern`/`enumeração`/faixa/tamanho
   que a Fase 3 vai acionar com mais frequência. Corrigido: `XsdValidator`
   reescrito para rastrear o elemento aberto via `XmlReader` durante a
   validação, produzindo XPath aproximado; teste novo
   (`Value_violating_schema_pattern_fails_xsd_validation_with_actionable_diagnostic`)
   força uma violação real de `pattern` (CNPJ inválido) e prova o
   diagnóstico. Suíte final: 22/22.

Achados informativos, sem ação necessária: `TemplateCompiler` ainda não
processa atributos sem valor fixo (já registrado em "Dívida técnica
aceita", não afeta a fixture atual); `XsdSchemaImporter` não valida path
traversal do caminho de arquivo recebido (correto para a Fase 2 — não há
superfície de upload não confiável ainda; isso é escopo formal da Fase 3).

Pontos fortes confirmados pela revisão: nenhuma dependência de
infraestrutura em `Domain`/`Xsd`/`Engine` (grafo de projetos estritamente
unidirecional, verificado nos `.csproj`); nenhum vazamento do escopo do
núcleo; `XmlDocumentBuilder` constrói genuinamente via `XElement`/
`XDocument` (confirmado lendo o arquivo inteiro, não só o teste);
`TemplateCompiler` compila para delegate via `System.Linq.Expressions`
(ADR-0001 cumprido de fato, não só nominalmente); distinção nillable-vs-
opcional correta e testada nos dois sentidos; nomes/responsabilidades dos
componentes batem com `docs/architecture/COMPONENTS.md`; testes verificam
conteúdo/estrutura do XML, não apenas um booleano de sucesso.

**Gate confirmado cumprido — Fase 2 pronta para commit.**

## Resultado da revisão independente da Fase 3

Executada por agente `reviewer` isolado em 2026-09-28. Verificação
independente de verdade: rodou `dotnet build` (0 erros, 0 avisos), `dotnet
test` (53/53) e `git status` ele mesmo. Foi além do pedido — escreveu e
executou 4 cenários de ataque adicionais não cobertos pelos testes
existentes, em um projeto scratch separado:

1. Include auto-referenciado — falha limpa, sem loop infinito.
2. Includes mutuamente recursivos (a→b→a) — resolvido corretamente
   (`XmlSchemaSet` deduplica por URI), sem loop infinito.
3. Path traversal com `..%2f` (URL-encoded) no `schemaLocation` — bloqueado.
4. Path traversal com barra invertida estilo Windows (`..\`) — bloqueado.

Nenhum bypass encontrado em nenhum cenário, incluindo os 4 adicionais.

Veredito: **GATE CUMPRIDO**, sem achado bloqueante. Um único achado
informativo: `docs/DECISIONS.md` já referenciava esta seção antes de ela
existir no arquivo — exatamente o tipo de descuido que causou o achado
crítico da revisão da Fase 0 ("documento cita algo que ainda não existe").
Esta seção agora existe, fechando a referência antes do commit.

Pontos fortes confirmados: determinismo de hash genuinamente testado (não
assumido), incluindo pacote ZIP vs. arquivos soltos produzindo o mesmo
`ContentHash`; testes de segurança forçam de fato o caminho perigoso (cada
fixture maliciosa declara um tipo usado pelo elemento raiz, evitando o
mascaramento silencioso já documentado nesta mesma fase); nenhuma
regressão nos testes da Fase 2 (mesmas asserções, só um `Assert.NotNull`
a mais pela nulabilidade de `Elements`); nenhuma violação de limite do
núcleo; nenhuma chamada de rede real em nenhum resolver; modelo de
partícula coerente com `docs/architecture/COMPONENTS.md` e ADR-0001;
`SourceModuleReference` cobre exatamente os exemplos da seção 10; fixture
ZIP contém exatamente os 2 arquivos esperados; documentação consistente
com o código entregue.

**Gate confirmado cumprido — Fase 3 pronta para commit.**

## Resultado da revisão independente da Fase 4

Executada por agente `reviewer` isolado em 2026-09-28. Rodou `dotnet
build`/`dotnet test`/`git status` ele mesmo, leu todo o código novo/
alterado e os testes, e foi além: reproduziu 2 cenários próprios fora dos
testes nomeados, num projeto scratch separado, referenciando os `.csproj`
reais — (1) erro de tipo dentro de uma `Condition`; (2) um XSD onde o
próprio elemento raiz (não um aninhado) tem conteúdo `xs:choice`.
Confirmou, lendo o código, que ADR-0001 é cumprido de fato: todo valor de
mapeamento (incluindo operadores compostos como
`concat(trim(A), lookup(B, tabela))`) passa por
`Expression.Lambda(...).Compile()` uma única vez em `TemplateCompiler`, e
`RuleExecutor` nunca percorre `RuleNode`/`OperatorNode` em runtime — só
invoca delegates já prontos.

Veredito: **GATE CUMPRIDO**, sem achado bloqueante. Achados não
bloqueantes, todos corrigidos mesmo assim antes do commit:

1. **[AVISO]** O cenário (2) acima revelou que o guard "mapear um
   elemento com conteúdo xs:choice/xs:all falha alto e claro" só existia
   para elementos aninhados (`CompileComplexItem`/`CompileCollectionItem`),
   nunca para o elemento raiz — nenhum fixture do repositório chegava a
   exercitar essa lacuna (o root de `exemplo-composto.xsd` é `xs:sequence`;
   só o "tomador" aninhado é `xs:choice`), e mesmo sem o guard o
   comportamento não chegava a gerar XML incorreto silenciosamente (falhava
   depois, na validação final, com diagnóstico menos amigável). Corrigido:
   `TemplateCompiler.Compile` agora aplica o mesmo guard ao elemento raiz
   quando o `MappingTemplate` tenta mapear qualquer coisa.
2. **[BAIXO]** O catálogo registrava 23 operadores, não 24 — faltava
   `constant` como entrada nomeada (coberto até então só implicitamente via
   `LiteralNode`), divergindo da contagem já documentada. Corrigido:
   `constant` registrado explicitamente (equivalente a `copy` aplicado a um
   literal), fechando a contagem em 24 de fato.
3. **[INFORMATIVO]** Erro de tipo dentro de uma `Condition` (não só dentro
   de `Value`) não tinha teste nomeado, apesar de o mecanismo já funcionar
   corretamente (confirmado pela reprodução do revisor). Corrigido: teste
   `Type_error_inside_a_condition_is_a_critical_diagnostic_not_a_crash`
   adicionado.

Achado informativo registrado em "Dívida técnica aceita", sem correção de
código necessária agora: `SourceDataTree.FromObject` roda reflection por
item de coleção, por geração — custo a medir na Fase 11, não uma violação
do ADR-0001.

Pontos fortes confirmados pela revisão: nenhum código arbitrário executável
(catálogo fechado, sem reflection-by-name vinda de dado de origem, sem
`eval`); extensibilidade real (operador `shout` registrado inteiramente
dentro de um teste, nenhum arquivo do núcleo tocado); coleções corretas
(0/1/N itens, filtro antes de cardinalidade, `$index`, origem não
enumerável) confirmadas contra XML real gerado; grupos lógicos funcionam
nos dois sentidos; zero regressão nas Fases 2–3; nenhuma dependência de
infraestrutura em `Domain`/`Xsd`/`Engine`.

**Gate confirmado cumprido — Fase 4 pronta para commit.**

## Resultado da revisão independente da Fase 5

Primeira rodada (agente `reviewer`, 2026-09-28): **GATE NÃO CUMPRIDO.**
Os dois testes do gate passavam e não eram vazios (gravador de comandos do
driver comprovadamente enxerga tráfego; nenhuma rota de geração escapa da
medição), mas o revisor reproduziu fora do repositório 3 bloqueantes:

- **B1 — API (JSON) × SDK (objeto C#) divergiam em regras sobre datas**, e
  a publicação validava um conteúdo diferente do servido (literal
  `DateOnly` virava texto na persistência; `DateOnly` × texto →
  `COMPARISON_INCOMPATIBLE_TYPES`). Corrigido: operadores de comparação
  tratam data × texto ISO como data; `SnapshotPublisher` valida/hasheia/
  grava a forma reidratada (`SnapshotJson.Rehydrate`); cenário de datas
  adicionado ao gate (JSON×JSON e JSON×objeto C#) com `success` esperado
  por cenário; testes diretos de operador. Mutação manual confirma que o
  gate falha sem a correção.
- **B2 — "um só ativo" dependia de índices que só a API criava**: um host
  do SDK sem `EnsureIndexesAsync` gravava 8 ativos versão 1. Corrigido:
  `MongoTemplateSnapshotRepository.PublishAsync` garante os índices antes
  da primeira publicação de cada instância; teste de 8 primeiras
  publicações concorrentes em banco novo sem setup de índice.
- **B3 — documentação dizia "0 avisos", build limpo tinha 1 (CS8604 em
  teste)**. Corrigido; números agora registrados a partir de
  `--no-incremental`.

Melhorias recomendadas aplicadas: idempotência por hash movida para
dentro da transação (publicações concorrentes do mesmo conteúdo convergem
— teste novo); Change Stream filtra só documentos que ficaram ativos
(antes eram 2 eventos e 2 recompilações por publicação); comentário de
`SourceDataTree` corrigido ("árvores equivalentes", não "a mesma árvore").

Melhorias recomendadas **não** aplicadas (registradas como dívida): cache
negativo para "não encontrado"/falha de compilação (vetor de
amplificação enquanto não há autenticação — Fase 9); `_versions` sem
limite; recarga sob `_refreshLock` usa `CancellationToken.None` (leitura
travada bloqueia trocas); Change Stream sem resume token (coberto pelo
poll pós-reconexão); nomes XSD que normalizam para o mesmo caminho se
sobrescrevem em silêncio; atraso de reconexão fixo (5 s) no
`TraduxRuntime`.

Segunda rodada (reverificação, mesmo agente `reviewer`): B2 e B3
confirmados corrigidos (scratch T2: 8 primeiras publicações sem índice →
versões distintas, 1 ativo; T3 sem regressão; 0 avisos com
`--no-incremental`). B1 confirmado para datas simples, mas **1 bloqueante
residual B1'**: data/hora com fuso (`xs:dateTime` com TZD — o formato de
emissão da NFS-e) e `DateTimeOffset` ainda divergiam entre JSON e objeto C#;
melhoria correlata: `equals` entre textos ISO de precisões diferentes
divergia do SDK. Corrigido pela opção de código (não por aceite de
limitação): comparação como instante `DateTimeOffset`, fuso `Z`/`±hh:mm`,
sem fuso = UTC nos dois caminhos, dois textos ISO comparados como data;
`concat` e o conversor de literais emitem ISO para `DateTime`/
`DateTimeOffset`. Gate ganhou data/hora com fuso no cenário `dps-datas`
(JSON × objeto C#), com mutação manual confirmando que falha sem a
correção; 4 testes de operador novos. Informativo da reverificação: o
filtro do Change Stream (`status = active`) não cobre desativação sem novo
ativo nem deleção — hoje inexistentes; registrar/tratar na Fase 7.

Terceira rodada (mesmo `reviewer`): B1' confirmado corrigido (T4: data/hora
com fuso e `DateTimeOffset` iguais nos dois caminhos; `equals` entre
precisões diferentes também). **Novo bloqueante B1''**: `formatDate` só
aceitava `DateOnly`/`DateTime` — texto ISO vindo de JSON dava
`FORMAT_DATE_INVALID_TYPE` enquanto o objeto C# funcionava. Em vez de
corrigir só esse operador, auditei todo o catálogo e achei a mesma classe
em operadores de texto (`substring`/`replace`/`trim`/`padLeft`/`padRight`
recusavam data C#), em `lookup` (`DateOnly` virava `09/28/2026`) e na
emissão de `xs:date`/`gYearMonth` (texto JSON passava cru). Correção por
contrato, não por caso: **objeto C# ≡ sua serialização `System.Text.Json`**
(ADR-0008 §5) — datas C# viram, em todo contexto de texto, o texto exato
que o `System.Text.Json` escreve (`DateTime` respeitando `Kind`);
`formatDate` e comparações aceitam texto ISO; `xs:date`/`gYearMonth`
normalizam texto ISO. O gate ganhou um teste que verifica o contrato
diretamente (`JsonSerializer.Serialize(obj)` pela API × `obj` pelo SDK,
para DPS, pedido e DPS com datas) e o fixture de datas passou a exercitar
`formatDate`, `substring`, `lookup` e `concat` sobre `DateOnly`/`DateTime`/
`DateTimeOffset`; mutação manual (operadores da 2ª rodada) derruba os 3
cenários de datas. Informativos da 3ª rodada registrados: mudança de
semântica texto×texto ISO (agora no ADR-0008 §5); `xs:dateTime` como
elemento de destino continua fora de escopo (dívida já registrada).

Quarta rodada (mesmo `reviewer`, com harness de 1956 casos objeto C# ×
`JsonSerializer.Serialize(objeto)` pelos 24 operadores e 4 tipos de
destino): **NÃO CUMPRIDO**, 5 bloqueantes contra o contrato declarado —
B4 data emitida em `xs:string` com formato americano pelo SDK; B5
`equals` entre `DateTime` de `Kind` diferente; B6 data × texto não-ISO
(erro no C#, ordinal no JSON); B7 erro de conversão na emissão virava
exceção (HTTP 500), inclusive `"ValorServico":"abc"`; B8 objeto/lista em
contexto escalar vazava nome de tipo CLR e item escalar de coleção expunha
propriedades CLR (`Length`, `Year`). Informativo: mensagens com nome de
tipo CLR diferiam entre caminhos (477 casos).

**Mudança de abordagem** (ADR-0008 §5 e "Alternativas"): a equivalência
passou a valer **por construção** — o SDK converte o objeto C# pelo
`System.Text.Json` (`SourceDataTree.FromClrObject`) e o lê pelo mesmo
`FromJson` da API; corrigir operador a operador tinha produzido um
bloqueante novo por rodada. Defeitos reais do motor, independentes do
caminho, corrigidos diretamente: conversão na emissão sem exceção
(`VALUE_CONVERSION_FAILED` com XPath; texto decimal sem separador de milhar
— `"1500,50"` não vira `150050`); delegates de regra embrulhados
(`RULE_EXECUTION_FAILED`), ordenação e resolução de coleção protegidas;
objeto/lista em operador de texto = `EXPECTED_SCALAR`; texto canônico único
(`CanonicalValueText`) para operadores e `xs:string`; item escalar de
coleção em `$value`; `equals` compara datas como instante antes do atalho
`Equals`; data × texto não-ISO = texto × texto; mensagens com nomes
neutros de tipo. Testes novos: matriz `EquivalenceContractMatrixTests`
(660 casos tipo × expressão × destino: nada lança exceção e objeto ≡ JSON,
mensagens incluídas), testes de B4/B5/B6/B8 no operador e na emissão, e
teste de API: `"abc"`, `"1500,50"` e objeto em `ValorServico` → 200 com
`VALUE_CONVERSION_FAILED`, nunca 500.

Quinta rodada (mesmo `reviewer`): **GATE CUMPRIDO.** Harness de 1956
casos `FromClrObject(obj)` × `FromJson(Serialize(obj))`: 1956 idênticos,
mensagens incluídas, nenhuma exceção saindo de `XmlGenerationEngine`.
B7 pela API real (Production): valores inválidos em decimal/data/texto,
objetos, listas, 100 mil caracteres, 200 níveis de aninhamento — sempre
200 com diagnóstico ou 400, nunca 500. `FromClrObject` com ciclo, NaN,
`Type`, `IntPtr` → `ArgumentException`. Sem regressão nas Fases 2–4.
Melhorias recomendadas, todas aplicadas antes do commit: corpo malformado
era 500 em Development (`BadHttpRequestException` com `ThrowOnBadRequest`)
→ `StatusCodeSelector` mapeia para 400, com teste; semântica do
`System.Text.Json` (`[JsonIgnore]`, tipo base, chaves não-string)
documentada no ADR-0008 §5; exceção lançada por getter agora também vira
`ArgumentException` em `FromClrObject`.

**Gate da Fase 5 cumprido e confirmado por revisão independente —
Fase 5 pronta para commit.**

## Resultado da revisão independente da Fase 6

Primeira rodada (agente `reviewer`, 2026-09-29): **GATE NÃO CUMPRIDO.**
Testes 228/228 e e2e "GATE OK" confirmados; fluxo principal funciona de
verdade (o revisor refez variações próprias no navegador e pela API,
inclusive `for each` aninhado com caminhos com ponto). XSS/CSP/upload/
arquitetura sem achado. Bloqueantes:

- **B1** — complexo obrigatório sem filho mapeado (`<itens>` no próprio
  fixture do gate): a tela dizia "todos mapeados" e o servidor reprovava
  (`MAPPING_MISSING_REQUIRED`); não havia como emitir `<itens/>` pela UI.
  Corrigido: `buildMappingTemplate` emite complexo obrigatório sob pai
  ativo mesmo vazio; teste Node e etapa do e2e (testar logo após mapear
  `numero`/`valorTotal` → válido com `<itens />`).
- **B2** — grupo (choice dentro de sequence) usava o caminho do pai,
  sobrescrevendo o status dele e oferecendo mapeamentos que o compilador
  sempre recusa. Corrigido: caminho próprio (`/R/(choice#1)`); conteúdo com
  grupo fica todo não mapeável (espelha `TemplateCompiler`); obrigatório
  não mapeável vira "bloqueado" e conta como pendente. Testes .NET (XSD
  inline) e Node.
- **B3** — limite de corpo prometido no backlog não existia. Corrigido:
  `Tradux:Design:MaxRequestBytes` (413) em `/v1/design/*`; teste de API.

Avisos também corrigidos: A1 foco perdido a cada ação (foco restaurado;
painel com texto em edição não é recriado; e2e verifica Enter no nó); A2
clique engolido após digitar e, achado ao corrigir, gravação de valor
antigo por closure capturada — editores passaram a ler/gravar o estado
atual por lentes (`js/ui/lens.js`), campos de texto usam `input`; e2e
digita de verdade e clica em seguida, sem `change` sintético; A3
diagnósticos duplicados (servidor não repete; UI deduplica); A4 rascunho
inválido quebrava o editor (validado antes de aplicar, `draftProblems`);
A5 mapeamentos órfãos (status "ignorado", remoção do bloco remove os
filhos); A6 `expectedXml` com DTD (lido sem DTD); A7 500 → 400 (arquivo
nulo, `entryPoint` ausente); A8 exportação do estado em memória; A9 "Nulo"
só em elemento nillable; A10 comparação aponta XPath e texto da primeira
diferença e usa o XML esperado do teste; A11 publicação pelo editor
registrada nas entregas da Fase 7 do ROADMAP; A12 elemento raiz digitado
preservado; CSP com `frame-ancestors`/`base-uri`/`form-action`/`object-src`;
CORS restrito a `/v1/design/*`; tabelas DE-PARA como linhas ordenadas
(chave editável sem misturar linhas; chave repetida bloqueia o teste).

Segunda rodada (mesmo `reviewer`): **GATE CUMPRIDO**, sem bloqueantes. Os
scripts do revisor (s1–s4, grp) confirmaram B1, B2, B3 e A1–A10 corrigidos;
rascunho antigo com tabelas em dicionário importa certo; XSS 0 elementos
injetados. Três avisos da renderização parcial, corrigidos antes do
commit: R2 (Enter no nome da tabela não redesenhava o painel) e R3
(painel de teste preso em "Testando…" com foco no XML esperado) — o painel
só é preservado quando a renderização vem da digitação no próprio campo,
e o cursor é restaurado quando um campo de texto é recriado; R7 (duas
linhas com chave vazia contavam como chave repetida). Etapa 8 no e2e
(Enter no nome da tabela). Informativo sem ação: com `drop` sintético e
foco em texto o painel de regra podia ficar no nó anterior — lentes
impedem corrupção e, com a correção de R2/R3, o painel redesenha.

**Gate da Fase 6 cumprido e confirmado por revisão independente — Fase 6
pronta para commit.**

## Resultado da revisão independente da Fase 7

Primeira rodada (agente `reviewer`, 2026-09-30): **GATE NÃO CUMPRIDO.**
Resolução, explicação, rollback, fallback e auditoria funcionam; os testes
do gate passavam. Bloqueantes:

- **C1** — propagação não aplicava a regra de acesso: versão nova NÃO
  compartilhada de camada de outra organização chegava ao cliente pelo
  rebase. Corrigido: `ChainAccessError` em toda avaliação
  (`LAYER_ACCESS_DENIED`); teste `C1_…`.
- **C2** — aprovar proposta velha revertia publicação mais nova; publicar
  e rollback não invalidavam propostas. Corrigido: publicação, vínculo e
  rollback marcam pendentes `Superseded`; aprovação confere a versão
  ativa da proposta (senão `Superseded` + recusa); 2 testes `C2_…`.
- **C3** — propagações concorrentes podiam deixar a versão mais velha ativa.
  Corrigido: concorrência otimista (`EnforceExpectedActive`/
  `ExpectedActiveVersion`, verificada na transação do MongoDB), propagação
  reavalia até 3 vezes; teste determinístico em memória (`C3_…`, gancho
  `BeforeGuardedPublish`) e corrida real contra `mongod`
  (`Guarded_publications_racing_…`: 6 publicações, só 1 passa).
- **C4** — explicação marcava como publicada regra inativa (bloco sem
  coleção) e o diff comparava entradas cruas. Corrigido: `Entries` do
  resolvido = entradas publicadas; regra inativa sem fonte final e com
  `Note`; diff usa as entradas publicadas do ativo.

Avisos, todos tratados:

- **A1** — exceção num vínculo abortava a propagação inteira: agora cada
  vínculo isolado, falha vira `UPDATE_UNEXPECTED_ERROR` (teste `A1_…`).
  Versão rebaseada sem uso quando a publicação falha depois de gravá-la:
  aceita e documentada (ADR-0010, alternativas).
- **A2** — publicação direta (Fase 5) numa chave vinculada fazia a
  propagação sumir em silêncio: agora `ACTIVE_WITHOUT_LAYERS` (teste `A2_…`).
- **A3** — nova versão do cliente sobre o pai novo falhava (cadeia montada
  a partir da ativa): agora vale a cadeia declarada pela camada (teste `A3_…`).
- **A4** — fornecedor recebia atualizações de outras organizações; gate de
  cenário gravava valores. Agora `LayerVersionResult` só traz as da
  organização que criou a camada; mensagens do gate só com código e XPath
  (teste `Scenario_failures_never_carry_scenario_values`).
- **A5** — índices de vínculos/atualizações/auditoria só no startup da API:
  agora `IndexGuard` antes da primeira escrita.
- **A6** — tratamento de proposta não atômico: a checagem de versão ativa
  na aprovação + publicação condicionada garantem que duas aprovações
  simultâneas da mesma proposta publicam **no máximo uma** vez. Resta
  (dívida aceita): o registro da proposta fica com o resultado de quem
  gravar por último — pode mostrar `Failed`
  (`PUBLICATION_CONCURRENT_CHANGE`) embora a outra aprovação tenha
  publicado; o snapshot ativo e a auditoria mostram a verdade. Resolve-se
  com transição condicional de status quando a escrita por HTTP chegar
  (Fase 9).

`CLAUDE.md` → "Projetos atuais" conferido: lista a Fase 6 (sem projeto
novo na Fase 7). Resultado: 263/263 após a 1ª rodada.

Segunda rodada (agente `reviewer`, 2026-09-30): **GATE CUMPRIDO.** C1–C4 e
A1–A5 confirmados fechados (inclusive rotas alternativas: aprovação,
reavaliação no apply, atalho "mesmo hash", retry da propagação); A6 aceita
como dívida. Build 0 avisos; 263/263. Avisos não bloqueantes:

- **W2** (cliente novo sobre pai antigo rebaixava o produto sozinho) —
  corrigido: cadeia declarada que rebaixa camada ativa vira proposta mesmo
  em política automática (`LAYER_DOWNGRADE`); teste `W2_…`.
- **W4** (supersede e `ACTIVE_WITHOUT_LAYERS` sem auditoria) — corrigido
  (`template.update.supersede`).
- **W5** (lançamento reprovado derrubava proposta válida) e **W6**
  (aplicação automática deixava propostas `Proposed`) — corrigidos; teste
  `W5_W6_…`.
- **W8** (número de testes desatualizado em "Riscos") — corrigido.
- Dívida registrada: **W1** `TraduxRuntime.PublishAsync` publica sem
  gates/auditoria — **resolver na Fase 8** (bloquearia o gate dela);
  **W3** rollback sem concorrência condicionada e **W7** lista de
  fallback sem limite — Fase 9 (junto com A6); **W9** versões rebaseadas
  não propagam para outros vínculos (coerente com ADR-0010, documentado).
- e2e do editor (não rodado pelo revisor; o `DraftDesignService` foi
  refatorado): rodado pelo coordenador antes do commit contra API + MongoDB
  replica set locais — `GATE OK`, todas as etapas 1–8.

**Gate da Fase 7 cumprido e confirmado por revisão independente.**

## Resultado da revisão independente da Fase 8

Primeira rodada (agente `reviewer`, 2026-09-30): **GATE NÃO CUMPRIDO.**
Build 0 avisos; 315/315; node 15/15. Caminho padrão correto (SDK direto,
camadas, atualização e aprovação passam pelo pipeline; relatórios sem dados;
sem innerHTML; sem escrita HTTP). Bloqueantes, todos corrigidos com teste
(`QualityReviewRegressionTests`):

- **B1** — `gates: []` substituía os gates obrigatórios (publicava sem
  estrutural/funcional). Agora compilação/estrutural/funcional são fixos;
  o parâmetro virou `additionalGates` (só soma); exceção num gate reprova
  (`GATE_EXECUTION_FAILED`).
- **B2** — um campo sem valor gerável (pattern fora do subconjunto) zerava a
  suíte e `Passed` era verdadeiro com 0 casos. Agora o campo fica fora dos
  casos; se alimenta obrigatório vira caso reprovado
  `SYNTHETIC_VALUE_UNAVAILABLE` sem mascarar outros defeitos; o gerador usa
  valor do cenário do autor antes de desistir; 0 casos nunca aprova (painel
  do editor idem).
- **B3** — `maxLength` enorme e repetição aninhada alocavam centenas de MB;
  faceta acima de `int` dava 500. Teto de geração (4096) com "não coberto";
  teto no gerador de pattern; leitor de facetas satura; o `OverflowException`
  do próprio compilador de schema do .NET vira `XmlSchemaException` (400).
- **B4** — `minLength ≥ 4` sem `maxLength` lançava exceção
  (`Math.Clamp`). Cálculo seguro.

Avisos: W1 (chave de DE-PARA em campo direto, limites de data, decimal com
limite exclusivo) corrigido; W2 (limite de tamanho com pattern, datas sem
inválido de limite) documentado como "não coberto"; W3 (ADR §3 e
PROJECT_STATE divergentes) alinhado; W4 (mudança decimal) registrada em
DECISIONS.md e ADR-0011 §8; W5 (gate avaliava conteúdo não reidratado)
corrigido; W6 (exceção sem auditoria) coberto pelo pipeline. Resultado:
325/325.

Segunda rodada (2026-09-30): **GATE NÃO CUMPRIDO.** B1, B2, B4, W1, W5,
W3/W4 confirmados fechados; bloqueante **B3-residual**: CPU sem limite no
gerador de pattern (`(a?){2000000000}` repetia conteúdo vazio sem emitir,
o teto de saída nunca disparava). Corrigido com orçamento de passos por
geração (`MaxSteps`); W7 (`Rehydrate` fora de `try` no publicador direto →
exceção sem auditoria) e W8 (gate adicional podia sobrescrever o relatório)
corrigidos. Testes de regressão para os três.

Terceira rodada (2026-09-30): **GATE CUMPRIDO.** Sondas de CPU/memória
todas em milissegundos; nenhum achado anterior voltou; build 0 avisos;
329/329. Aviso W9 (pattern com retrocesso catastrófico estourava o timeout
de regex e dava 500 no endpoint de desenho) corrigido antes do commit:
vira "não coberto". Limite de complexidade de pattern na importação (o
validador XSD do .NET não tem timeout — risco anterior à Fase 8 que também
afeta `/v1/design/test`) registrado como dívida da Fase 9. e2e do editor
rodado pelo coordenador após as correções: GATE OK (14 de 14 casos).
Resultado final: 330/330.

**Gate da Fase 8 cumprido e confirmado por revisão independente.**

## Resultado da revisão independente da Fase 9

Primeira rodada (agente `reviewer`, 2026-09-30): **GATE NÃO CUMPRIDO.**
Build 0 avisos; 368/368; node 17/17. Isolamento por rota, ator, cripto da
captura, telemetria só com códigos e editor confirmados corretos.
Bloqueantes:

- **B1** — `PatternSafetyChecker` deixava passar `(a*){1,1000}`,
  `(a?){1000}a{1000}`, `(.*a){20}`, `(a|aa)+`; o validador XSD do .NET não
  tem timeout → thread presa (DoS entre organizações via `/v1/design/test`
  ou snapshot publicado). Corrigido em três camadas: `RegexTimeout.Configure()`
  no host (1 s); `PatternGuard` no motor pré-checa cada valor com pattern
  com timeout explícito → `XSD_VALIDATION_TIMEOUT` sem chegar ao validador
  (determinístico mesmo quando o timeout do processo já foi fixado por
  outro código — o caso do processo de teste); checker recusa repetição
  (máx. > 1) sobre grupo com quantificador variável. Testes: checker
  (+4 formatos), `PatternGuard`, e `/v1/design/test` com `(a|aa)+`
  terminando em segundos com o diagnóstico.
- **B2** — SDK `TraduxAccess` prometido não existia (sem caminho de
  revogação). Implementado (emitir, listar, revogar; auditado); teste
  revoga credencial inicial e prova que recolocá-la na configuração não a
  reativa.

Avisos tratados: W1 (camada alheia indistinguível de inexistente — mesma
mensagem e código; teste), W2 (varredura agora em Debug de verdade, com
400s e captura ligada), W3 (`correlationId` com formato/tamanho), W4
(máscara de XML herda do ancestral + abreviações do leiaute nacional),
W5 (`shared=true` exige `template.publish`, flag auditado), W6 (ator
com id, revogada não volta ao cache, cache negativo separado), W7
(`MatchedCount`), W8 (contador zera em janela nova; `keyId` — rotação
não quebra a leitura), W9 (docs/comentários), W10 (aviso da credencial de
desenvolvimento). Registrados como limite: sem limitação de taxa, limite
de captura por processo, proposta presa em `Applying` em queda. Resultado:
380/380.


Segunda rodada (2026-09-30): **GATE NÃO CUMPRIDO.** B1, B2 e W2–W10
confirmados (camada 1 do timeout provada na API real: `(\c|\c\c)+` →
`XSD_VALIDATION_TIMEOUT` em 1,2 s). Bloqueante **N1**: o resto do W1 — a
auditoria da recusa gravava a cadeia inteira carregada, revelando à
org-b a existência e os pais privados de camadas da org-a. Corrigido:
`LoadChainAsync` checa acesso a cada passo (topo × organização que pede,
pai × dono do filho) antes de carregar o próximo e a recusa cita só o ref
pedido; teste compara a auditoria da org-b (existente × inexistente) e
prova que o pai privado não aparece. Avisos tratados: A1 (timeout na
compilação do schema com `default`/`fixed` → `XmlSchemaException`/400),
A3 (cache do `PatternGuard` limitado a 1024 patterns), A4 (revogação
durante a consulta desfaz o cache). A2 (`AppContext` por ordem de
inicialização) mantido: é a 1ª linha do `Program` no processo real — o
`PatternGuard` cobre o caso em que o timeout global não vale. 380/380.

Terceira rodada (2026-09-30, revisor novo — o anterior parou sem entregar):
**GATE NÃO CUMPRIDO.** N1 confirmado fechado (rota direta), A1/A3/A4
corretos, nenhuma regressão; 380/380, node 17/17. Bloqueante **N2**: a
propagação gravava, no histórico e na auditoria da organização
consumidora, a atualização disparada por versão NÃO compartilhada de
camada de outra organização (`Trigger = org-a/…@N`) — a org-b acompanhava
os lançamentos privados da org-a. Corrigido: `PropagateAsync` ignora
vínculos de outra organização quando a versão lançada não é compartilhada
(ela nunca entraria na cadeia deles). Testes: C1 agora exige ausência de
atualização e auditoria no cliente; caso de rebase (base v2 privada sob
cliente de outra organização) e checagem por HTTP de `…/updates` e
`…/audit` da org-b. Avisos registrados: `_revokedHere` cresce uma entrada
por revogação (impacto baixo); mensagem de pai indisponível cita o topo;
host do SDK deve chamar `RegexTimeout.Configure()` (também na importação
de XSD com `default`/`fixed` patológico).

Quarta rodada (2026-09-30): **GATE CUMPRIDO.** N2 confirmado (filtro exato,
antes do `try`; vínculos da própria dona propagam normalmente); lineage,
explicação, avisos de downgrade e rotas diretas sem vazamento; 382/382,
node 17/17. Avisos tratados antes do commit: a metade `…/updates` do teste
HTTP passou a desserializar o `trigger` (antes comparava texto que nunca
aparece naquele formato); lacuna de numeração de versões (`@1` → `@3`)
registrada no ADR-0012 como limite conhecido. e2e do editor rodado pelo
coordenador após as correções: GATE OK (etapas 0–10).

**Gate da Fase 9 cumprido e confirmado por revisão independente.**

## Resultado da revisão independente da Fase 10

Primeira rodada (agente `reviewer`, 2026-09-30): **GATE NÃO CUMPRIDO.**
Hash preservado entre ambientes, RAG com fonte/versão, separação do RAG da
geração e matriz de rotas confirmados. Bloqueante B1: a exportação levava
camadas não compartilhadas de outra organização da cadeia (vazamento de
conteúdo entre tenants). Avisos: A1 importação só com `template.import`;
A2 camadas importadas sem validação/desfazer e ADR exagerando a garantia;
A3 hash conferido só depois de publicar; A4 JSON malformado → 500 sem
auditoria; A5 snapshots anteriores à Fase 10 sem cenários; A6 `Lazy` do RAG
guardava a exceção, gravação não atômica; A7 SDK sem gates adicionais;
A8 PROJECT_STATE dizia que `docs/rag/` era esqueleto.

Correções: B1 — camada de outra organização só viaja como referência + hash
e precisa existir idêntica no destino (teste
`B1_foreign_private_layers_never_travel_in_the_package_but_the_hash_is_still_preserved`);
A1 — importação exige também `template.publish` + `template.create`
(403 para editor/auditor testado); A2 — `Kind` conferido, ADR-0013 §5
reescrito, risco aceito; A3 — hash do snapshot pré-calculado antes de
qualquer gravação (teste A3_A4); A4 — erro de JSON vira
`PackageValidationException` auditada; A5 — fallback para os cenários do
vínculo (teste A5) + runbook; A6 — build sob lock sem cache de falha,
gravação temporário+troca; A7 — risco aceito; A8 — corrigido.
Resultado após as correções: build 0 avisos, 405/405.

Segunda rodada (mesmo revisor): **GATE CUMPRIDO** (0 avisos; 405/405;
node 17/17), sem bloqueantes. Avisos tratados antes do commit: R1 — pacote
com camadas forjado com hashes recalculados trocava a versão ativa antes de
detectar o hash divergente; agora a cadeia é resolvida em memória a partir das
camadas do pacote e o hash do snapshot é conferido contra ela antes de gravar
(teste `R1_forged_layer_with_recomputed_hashes_is_refused_before_changing_anything`);
R2 — todas as camadas são validadas numa 1ª passada e só gravadas depois
(asserção no teste de integridade); R3 — `binding.json` lido na validação
inicial e exceções de conteúdo inválido viram recusa auditada. Resultado
final: build 0 avisos, 406/406.

**Gate da Fase 10 cumprido e confirmado por revisão independente.**

## Resultado da revisão independente da Fase 11

Primeira rodada (agente `reviewer`, revisão final do sistema, 2026-09-30):
**GATE NÃO CUMPRIDO.** Build 0 avisos; 421/421; metas do ADR-0014
reproduzidas pelo revisor (200/min p99 6,2 ms; 2.000/min p99 5,2 ms;
saturação ~259 mil/min; 0 comandos em snapshots) e sockets reais
confirmados. Limites do núcleo, autorização por rota, filtro de
organização, importação segura e logs sem payload confirmados no sistema
inteiro. Bloqueante B1: o limite de corpo de `POST …/xml` era aplicado por
texto do caminho (`EndsWith("/xml")`) e era contornado por `/XML`, `/xml/`
(corpo de 22,9 MB lido e ~2,7 GB alocados antes do 400). Avisos: 1 limite
de nós só depois de montar a árvore (~100× de amplificação); 2 caminhos de
geração sem limite (design test, `TraduxClient`) e documentação
exagerando a cobertura; 3 carência de credencial liberada por qualquer
exceção e brecha de revogação em partição não documentada; 4 decisão da
carência fora de ADR e textos "revogação em 30 s"; 5 "zero leitura" medido
mais estreito que o escrito; 6 limiares de tempo frágeis em CI e `First`
que podia lançar; 7 sem teste da configuração de produção do cliente
MongoDB; 8 classificação do 503 larga (regex) e estreita (eleição); 9
trechos desatualizados no PROJECT_STATE.

Correções: B1 — limite aplicado pelo endpoint roteado (metadado de nome
`GenerateXml`), regressão `/XML`, `/Xml`, `/xml/` → 413; 1 —
`GenerationLimits.Enforce(JsonElement)` antes de montar a árvore (teste de
equivalência com `Measure`); 2 — limites em `/v1/design/test`,
`TraduxDesigner` e `TraduxClient`; exclusões documentadas (gates
de publicação, `/v1/design/quality`); 3 — `CredentialStoreUnavailableException`
(adaptador MongoDB traduz só falhas de disponibilidade) é a única que libera
a carência (teste: outro erro sobe); partição documentada; 4 — ADR-0014 §4,
nota no ADR-0012, `CredentialService`, INSTALACAO e FALHAS atualizados; 5 —
ADR-0014 §2 e PERFORMANCE.md dizem o que é medido e listam as leituras de
fundo; 6 — `TestTiming.Slack` (4× com `CI=true`), `FirstOrDefault` com
mensagem; 7 — `MongoResilienceSettingsTests` (padrão 5 s, string de conexão
prevalece); 8 — `MongoAvailability.IsUnavailable` (exclui regex, inclui
eleição, teste); 9 — trechos corrigidos. Resultado: build 0 avisos, 429/429.

Segunda rodada (mesmo revisor): **GATE CUMPRIDO** (0 avisos; 429/429), sem
bloqueantes; sondas independentes confirmaram 413 em `/xml`, `/XML`,
`/Xml`, `/xml/`, `/%78ml` e corte do corpo chunked, recusa de array
gigante sem montar a árvore, carência só por indisponibilidade. Achados
informativos tratados antes do commit: comentário de documentação fora do
lugar em `AccessModel.cs` e leitura dupla do texto em
`GenerationLimits.Enforce(JsonElement)`; a dupla verificação (JSON e
árvore) no caminho JSON fica (custo limitado ao orçamento de nós).
Resultado final: build 0 avisos, 429/429.

**Gate da Fase 11 cumprido e confirmado por revisão independente — roadmap concluído.**

## Resultado da revisão independente da Fase 12

Rodada única (agente `reviewer`, 2026-09-30): **GATE CUMPRIDO**, sem
bloqueantes. Build 0 avisos; 451/451. Confirmado com código de `34dc6d4`
num worktree temporário: hashes de snapshot e de camada sem XSD de origem
idênticos; pacote exportado pela versão anterior importa com hash preservado.
Segurança do XML de entrada (DTD/XXE, `schemaLocation` ignorado, limites),
autorização de `/transform` e `/v1/design/source-schema`, limite de corpo
pelo endpoint, ausência de valores em logs/gates, camadas e editor (sem
`innerHTML`, CSP intacta) confirmados.

Avisos tratados antes do commit: 1 — `xsi:nil` inválido dentro de
`xs:any` lançava `FormatException` (500): agora só "true"/"1" é nulo; 2 —
`xs:unique`/`xs:key` passam a ser validados
(`ProcessIdentityConstraints`); 3 — teto de 20.000 campos na forma da
origem (XSD pequeno com expansão exponencial recusado em milissegundos); 4
— atributos com o tipo do XSD também na leitura (editor e leitor iguais);
5 — `$value` na raiz lido; 6 — teste de integração com camada base com XSD
de origem por HTTP e MongoDB real (cache frio); 10 — editor lê XML de
arquivo na codificação declarada; 11 — comentário de documentação no lugar;
12 — XML nulo no SDK vira `ArgumentException`. Avisos 7, 8, 9 e a parte
de 12 sobre remoção do XSD de origem ficaram como riscos aceitos (ADR-0015 e
"Riscos aceitos da Fase 12"). Testes de regressão: Xsd +4, Application +1,
integração +1.

**Gate da Fase 12 cumprido e confirmado por revisão independente.**

## Correção pós-Fase 12 — XSDs reais de NFS-e (2026-10-01)

Primeiro uso real do editor: o XSD de origem do usuário importa
`xmldsig-core-schema.xsd` (pacote NFS-e Nacional 1.01). Problemas
encontrados e corrigidos (ADR-0015 §5): seleção de XSDs só substituía
(agora acumula); xmldsig oficial do W3C (DOCTYPE com entidade interna)
recusado (agora `SafeDoctype` restrito); destino não aceitava `ref`,
`xs:any`, conteúdo simples com atributo nem `xs:integer` (agora opaco/texto);
raiz ambígua por causa do XML-DSig (agora automática). Com os arquivos reais:
DPS e NFSe importam como origem e destino.

Revisão de segurança independente (1ª rodada): **NÃO APROVADO** — B1:
expansão de entidades montava tudo em memória antes do limite (1,7 MB de
entrada → ~2 GB); avisos: regex com timeout virando 500 e ATTLIST
quadrático, documentação prometendo destino com xmldsig real, `ref` para
outro namespace, total do pacote não reconferido, expansão em
comentário/CDATA e redeclaração de `lt`, codificação, testes negativos,
estabilidade. Correções: `SafeDoctype` reescrito com leitura linear sem regex,
limite durante a expansão, total reconferido, predefinidas protegidas,
comentário/CDATA intactos, codificação recusada explicitamente; destino com
xmldsig real suportado (opaco); testes negativos (amplificação com
tempo/alocação, dois DOCTYPEs, `%e;`, `lt`, codificação, ATTLIST sem fim, ZIP
malicioso) e de motor (assinatura opcional omitida; obrigatória acusada).
Estabilidade: teste de telemetria da Fase 9 espera por condição e falha se não
chegar; janela curta de carga confere p95 com menos de 100 amostras (a
execução registrada de 60 s confere p99).

Rodada 2 (novo revisor): **NÃO APROVADO** por B1 — `OverflowException` (500)
com `maxOccurs` acima de `int` (novo ramo de `xs:any` e casos antigos de
elemento/grupo); XXE, amplificação (≤ 5 MB, ms) e conteúdo opaco resistiram
às sondas. Corrigido: estouro vira `XmlSchemaException` (400); avisos 1–4:
UTF-8 estrito, aspas/`>` recusados no valor da entidade, busca de entidade
sem alocação por `&`, DOCTYPE só no prólogo. Testes +4 (Xsd 92).
Rodada 3: **APROVADO** — todas as sondas repetidas; restam avisos não
bloqueantes: mensagens de erro de import às vezes enganosas (status 400
correto; o handler de resolução engole a falha de um arquivo importado) e
pior caso de ~730 ms de CPU por arquivo de 5 MB só de declarações ENTITY.

**Limite conhecido**: o motor de destino ainda não gera `xs:choice`; no DPS
1.01, 3 obrigatórios dentro de choice (`infDPS/prest`, `serv/locPrest`,
`valores/trib/totTrib`) impedem publicar um template que gere DPS.
Próximo passo proposto ao usuário: suporte a `xs:choice` no destino.

Resultado: build 0 avisos; `dotnet test` 478/478 (Domain 38, Xsd 92,
Application 189, Engine 58, Knowledge 6, Integração 78, Arquitetura 16,
Web 1 + 21/21 `node --test`).
