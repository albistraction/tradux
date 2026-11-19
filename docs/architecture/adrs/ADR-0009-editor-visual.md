# ADR-0009: Editor visual — HTML/CSS/JS sem build, fachadas de desenho sem estado

- **Status**: aceito
- **Data**: 2026-09-29
- **Fase**: 6 — Editor visual

## Contexto

A Fase 6 entrega o editor de DE-PARA (seção 12 do requisito de origem):
árvores de origem e destino, DE-PARA por seleção ou arraste, query builder
de condições (E/OU/negação), constantes/defaults/transformações/omissão,
blocos `for each` com filtro e listas aninhadas, marcação de obrigatórios
sem mapeamento e de erros, teste com dados de exemplo e comparação de XML.
Gate: "um usuário cria e testa um template sem editar JSON manualmente". A
seção 7 fixa "HTML, CSS e JavaScript simples no editor inicial";
`CONTAINERS.md` fixa que `Tradux.Web` fala **só HTTP** com a API. A
publicação com camadas e gate de testes é da Fase 7, e autorização é da
Fase 9 — a API ainda não autentica.

## Decisão

1. **Stack**: módulos ES nativos (`wwwroot/js`), CSS puro, sem framework,
   sem etapa de build e sem dependências npm. `Tradux.Web` é um host
   ASP.NET Core mínimo de arquivos estáticos + `/config.js` (endereço da
   API) + cabeçalho CSP restrito (`script-src 'self'`, nada inline, nada
   externo). Todo texto de dados entra no DOM como `textContent` (helper
   `h()`), nunca `innerHTML`.
2. **Separação lógica pura × UI**: `js/model/*` (expressões → `RuleNode`,
   query builder → `and`/`or`/`not`, inferência da árvore de origem,
   montagem do `MappingTemplate`, situação dos nós, escopo de `for each`)
   não toca o DOM e é testado com `node --test` (rodado dentro do
   `dotnet test` por `Tradux.Web.Tests`). `js/ui/*` só renderiza.
3. **Fachadas de desenho sem estado** (API `/v1/design/*` e SDK
   `TraduxDesigner`, mesmo `DraftDesignService`): listar operadores
   com metadados (`RuleOperatorMetadata` — informativo, a compilação não
   muda), descrever a árvore de destino de XSDs enviados em Base64
   (importação em memória com as proteções da Fase 3) e testar um rascunho
   (compila, gera, compara XML esperado semanticamente). Nada é gravado e
   MongoDB não é acessado. O resultado diz `publishable`, mas **não há
   publicação pelo editor** nesta fase.
4. **Árvore de origem inferida dos dados de exemplo** (JSON colado ou
   arquivo): não há ainda contrato de origem tipado persistido; os nomes e
   tipos vêm da amostra (união das chaves dos itens de listas). Os dados
   de exemplo são dado de teste, não o template — o gate proíbe editar o
   JSON do template, não informar dados.
5. **Caminhos com ponto no motor**: `SourceDataTree.TryGetValue("Tomador.Cnpj")`
   navega objetos aninhados (JSON e C#); chave exata com ponto tem
   precedência; intermediário nulo = presente e nulo; membro inexistente =
   ausente. Necessário para mapear fontes reais sem achatar.
6. **Rascunho no navegador**: estado do editor em `localStorage` e
   exportável/importável como arquivo — até a Fase 7 introduzir rascunhos
   persistidos com camadas.
7. **CORS** da API por configuração (`Tradux:Cors:AllowedOrigins`), aplicado
   só a `/v1/design/*`; vazio = sem CORS. Development libera
   `http://localhost:5230`. Corpo de `/v1/design/*` limitado
   (`Tradux:Design:MaxRequestBytes`, padrão 28 MB = pacote XSD máximo em
   Base64 + folga; acima disso, 413).
8. **Estado da UI sem valores capturados** (revisão da Fase 6): cada
   renderização recria os painéis a partir do estado, exceto o painel cujo
   campo de texto está sendo digitado naquele instante (o evento `input` já
   mantém o estado em dia), e
   devolve o foco ao controle na mesma posição; os editores leem e gravam
   pelo estado atual através de "lentes" (`js/ui/lens.js`), nunca por um
   valor capturado na renderização. Isso evita perder foco a cada ação,
   cliques engolidos após digitar e gravação de valores antigos.
9. **A árvore de destino espelha o compilador**: conteúdo com
    `xs:choice`/`xs:all` ou com grupo sem elemento próprio fica todo não
    mapeável (o `TemplateCompiler` recusa o conteúdo inteiro); grupos têm
    caminho próprio (`/R/(choice#1)`); complexo obrigatório é emitido
    mesmo sem filho mapeado; obrigatório não mapeável aparece como
    "bloqueado" e impede o rascunho de ficar verde.
10. **Verificação do gate no navegador**: `tests/Tradux.Web.Tests/e2e/golden-path.mjs`
   dirige o editor real num Chrome/Edge headless pelo protocolo DevTools
   (WebSocket nativo do Node): envia o XSD como arquivo, mapeia por clique
   e arraste, cria `for each` com filtro no query builder, monta uma
   transformação, testa e confere o XML; também verifica a marcação de
   obrigatório sem mapeamento e o diagnóstico que leva ao campo. Exige API
   + editor + MongoDB rodando, por isso fica fora do `dotnet test`.

## Alternativas consideradas

- **Blazor ou React/TypeScript**: contraria a seção 7 ("JavaScript simples
  no editor inicial"); React/TS exigiria toolchain npm e build; Blazor
  puxaria o editor para dentro do ecossistema .NET e tentaria referenciar
  projetos internos, violando `CONTAINERS.md` — rejeitadas por ora.
- **API servindo o editor (mesma origem, sem CORS)**: mais simples de
  implantar, mas mistura o contêiner do editor com o de runtime e
  contraria o diagrama de contêineres — rejeitada.
- **Endpoint HTTP de publicação já na Fase 6**: exigiria autorização
  (Fase 9) e o modelo de camadas/gate de testes (Fase 7) — adiado.
- **Contrato de origem inferido no servidor**: sem ganho — a inferência é
  apresentação; o motor não depende dela.
- **Playwright/Puppeteer para o teste de navegador**: dependência npm
  pesada; o protocolo DevTools direto cobre o necessário sem dependências.

## Consequências

- O editor funciona sem build: editar `wwwroot` e recarregar.
- Conteúdo `xs:choice`/`xs:all` aparece na árvore de destino como não
  mapeável (limite do motor desde a Fase 4, ainda sem ADR de resolução).
- Nomes de campos da árvore de origem dependem da amostra: campo que não
  aparece na amostra não é oferecido (o caminho ainda pode existir em um
  rascunho importado; o editor o mostra como "não está na amostra").
- Enquanto não há autorização (Fase 9), `/v1/design/*` aceita XSD de
  qualquer chamador — mitigado pelos limites e proteções de importação; a
  API não deve ser exposta fora de rede confiável.
