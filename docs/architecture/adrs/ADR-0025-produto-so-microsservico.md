# ADR-0025: O produto é só o microsserviço tradutor de XML

- **Status**: aceito
- **Data**: 2026-11-16
- **Fase**: 22
- **Substitui**: ADR-0019, ADR-0021, ADR-0022, ADR-0023 e ADR-0024 (conector de assinatura,
  conversor C# gerado, runtime netstandard2.0/.NET 4.8)
- **Substitui em parte**: ADR-0008 (a fachada SDK; o runtime sobre snapshot continua), ADR-0016
  (catálogo, rotas e tela de contratos de origem; ver §4) e ADR-0018 (conversor gerado; choice e
  eventos continuam)

## Contexto

Até a Fase 21 o motor era entregue de três formas: a API HTTP, o SDK em processo
(`Tradux.Sdk`, compilado também para netstandard2.0 e rodando no .NET Framework 4.8
do ERP) e o conversor C# gerado a partir do template (arquivo único ou padrão ERP).
O usuário redirecionou o produto em 2026-11-16:

> "Transforme esse projeto em um microserviço .NET em que possam ser cadastrados os templates
> e que vai ser o tradutor de XML. Eu envio um XML num formato tal para o template tal e ele
> me retorna o XML já convertido. Não vai ser mais uma DLL separada, vai ser apenas um
> microserviço. […] O importante é a tela inicial focar no XSD: eu só crio o DE-PARA e depois
> o microserviço traduz. Isso vai ser versionado pelo nosso próprio microserviço, não pelo Git."

Decisões tomadas pelo usuário nesta sessão:
- o conector de assinatura XML também sai (quem chama assina);
- as versões ficam só no serviço: a tela inicial lista os templates, abre uma versão, publica
  a próxima, mostra o histórico e reativa uma anterior; importar/exportar rascunho em arquivo sai;
- a tela parte só de "XSD de origem" e "XSD de destino"; os blocos próprios e o cadastro de
  contratos de origem saem.

## Decisão

1. **Entrega única: o microsserviço** (`Tradux.Api` + editor `Tradux.Web`).
   Saem do repositório: `Tradux.Sdk`, a CLI `tradux-codegen`, `Application/CodeGeneration`
   e as rotas `…/codegen/*`, `connectors/Tradux.Connectors.XmlSignature`, `build/Portable.props`
   (alvo netstandard2.0) e os testes em net48. Nenhum projeto é empacotável (`IsPackable=false`).
   As classes do antigo SDK continuam em `tests/Shared/Drivers` só como atalho dos testes,
   sem ser API pública.

2. **API do microsserviço** (`ServiceEndpoints`). A organização vem sempre da credencial,
   nunca da rota. A operação é `default`, salvo `?operation=`.
   - `GET /v1/templates` (`template.read`): templates com versão ativa (porta `ITemplateCatalog`,
     só o ponteiro ativo, índice `ix_org_status`).
   - `PUT /v1/templates/{nome}` (`template.publish`): cadastra a próxima versão (corpo igual a
     `…/publications`: XSD de destino, XSD de origem, mapeamento, tabelas, cenários). Os gates
     são os mesmos de qualquer publicação. 200 = nova versão ativa; 422 = recusada, a ativa não
     muda. O nome do template e o da operação seguem `^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$`.
   - `POST /v1/templates/{nome}/convert` (`template.use`): o corpo é o XML de origem.
     - 200: devolve o **XML de destino puro** (`application/xml`), com `X-Template-Version` e
       `X-Correlation-Id`;
     - 422: XML fora do XSD de origem ou regra que falhou (JSON com `diagnostics`);
     - 404: template sem versão;
     - `?format=json` devolve sempre o `GenerationResult`.

     Valem `version`, `versions` (fallback) e `correlationId`, com o mesmo limite de corpo da geração.
   - `GET /v1/templates/{nome}/versions` e `GET /v1/templates/{nome}/versions/{v}`
     (`template.read`): histórico e conteúdo completo de uma versão, que é o que o editor abre.
   - `POST /v1/templates/{nome}/rollback` (`template.publish`): reativa uma versão.

   As rotas detalhadas `/v1/organizations/{org}/templates/{t}/operations/{op}/…` continuam
   (camadas, pacotes, auditoria, diagnóstico). As do microsserviço só delegam aos mesmos
   serviços de `Application`, sem regra própria.

3. **Versionamento pelo serviço.** Cada publicação aprovada é um snapshot imutável e numerado
   no MongoDB (ADR-0003/0004). Rollback reativa um snapshot anterior. Publicar ou reativar
   atualiza na hora o cache da instância que atendeu (leitura da própria escrita); as demais
   instâncias recebem a mudança pelo Change Stream ou pelo poll (ADR-0005). O navegador só
   guarda a edição em andamento, para não perdê-la ao recarregar — nunca como versão.

4. **Entrada = XSD de origem.** O editor parte de "1. XSD de origem" e "2. XSD de destino" e
   segue com "3. DE-PARA → 4. Testar → 5. Publicar". O XML de exemplo e o XML esperado viram o
   cenário obrigatório da versão. Saem o catálogo de contratos de origem (`SourceContracts`,
   repositório Mongo, rotas `/source-contracts`, `/v1/design/source-contract`) e os campos
   `sourceExtensions`/`sourceContract` da publicação. O motor continua **lendo** extensões e a
   referência de contrato gravadas em snapshots antigos, para que eles sigam convertendo. Nada
   novo as grava, e a publicação descarta a referência.

5. **Abrir uma versão no editor** (`model/openVersion.js`): o `MappingTemplate` volta ao
   formato de mapeamentos por caminho:
   - `setDefault(valor, constante)` volta a ser valor + padrão;
   - and/or/not/comparações voltam ao construtor de condições;
   - grupos (`choice`/`sequence`) são transparentes.

   O que não tem destino na árvore ou não cabe no construtor é listado ao abrir: nada some
   em silêncio. Gate: ida e volta (`buildMappingTemplate ∘ mappingsFromTemplate`) é a identidade.

6. **Assinatura.** Fica com quem chama o serviço. A regra de importação que torna `ds:Signature`
   opcional no XSD de destino continua: o XML convertido sai sem assinatura e valida.

7. **Implantação.** `deploy/Dockerfile.api`, `deploy/Dockerfile.web` e
   `deploy/docker-compose.yml`: MongoDB replica set de um nó, API e editor. A credencial inicial
   entra só pelo hash, via `deploy/.env`.

8. **Refatoração de 2026-11-17 (decisões do usuário).**
   - Sai a entrada por objeto C# (`SourceDataTree.FromClrObject`, leitura por reflexão): pela HTTP
     só chegam JSON e XML, e a árvore interna só contém o que eles produzem (dicionário, lista,
     texto, decimal, booleano, nulo). Item de coleção que não é objeto (inclusive lista dentro
     de lista) fica em `$value`.
   - Saem os eventos de geração (`GenerationEvents`): sem SDK em processo não há quem os assine.
     Pós-processamento do XML é de quem chama o serviço.
   - Publicar ou ativar por qualquer caminho (conteúdo, camadas, aprovação, pacote, rollback)
     atualiza o cache da instância: o repositório de snapshots da API é um decorador
     (`CacheRefreshingSnapshotRepository`). Os endpoints não sabem do cache.
   - As rotas detalhadas que repetem as do microsserviço (`…/transform`, `…/versions`,
     `…/rollback`, `…/publications`) continuam e usam os mesmos handlers (`TemplateRequests`).
   - Publicação com um núcleo só (`Application/Publishing/SnapshotPublisher`): prepara o
     conteúdo uma vez (forma gravada + compilação + XSD de origem), os gates avaliam esse
     candidato e a gravação o reaproveita (hash do XSD incluso). Conteúdo, camadas e pacote só
     decidem o que publicar e como auditar. Os gates são fixos e em ordem (compilação →
     estruturais → cenários); o ponto de extensão `IPublicationGate`/`additionalGates`, que
     nunca teve implementação, saiu (substitui o que ADR-0010/0011 diziam dele).
     `PublicationResult` e `PUBLICATION_CONCURRENT_CHANGE` moram em `Publishing`.
   - Um só leitor de XML de origem (`Generation/SourceInputs`): limites de tamanho e de árvore,
     prazo de validação e diagnósticos `SOURCE_XML_INVALID` iguais na conversão, no teste do
     editor, nos gates e na regressão de atualizações.
   - Auditoria por um helper único (`Ports/AuditTrail`, com `AuditEvent`/`IAuditSink`);
     cenários funcionais em `Testing`. Segurança e diagnóstico não importam `Layers`.
   - Snapshots novos não gravam `contractId`/`contractVersion`; o leitor MongoDB ignora esses
     campos em snapshots antigos. Índices: um mecanismo (cada repositório garante os da sua
     coleção antes da primeira escrita); sai o inicializador de startup.
   - Limite de corpo declarado por rota (`WithBodyLimit`): as que recebem XSD em Base64
     (`/v1/design/*`, `PUT /v1/templates/{nome}`, `…/publications`, `…/schema-changes`,
     `POST …/layers`) usam `Tradux:Design:MaxRequestBytes` — antes o `PUT`, `…/publications` e
     `…/layers` ficavam só com o limite do servidor. Configuração por opções tipadas.
   - CORS só nas rotas que o editor usa (`/v1/design/*`, `/v1/templates`).
   - `KnowledgeService` mora em `Tradux.Knowledge` (o host passa o catálogo de operadores).

## Consequências

- O ERP (.NET 4.8) passa a consumir o serviço por HTTP. O conversor C# gerado e o
  runtime em processo deixam de existir. Um template antigo do ERP continua convertendo pelo
  serviço (o snapshot é autocontido).
- Testes removidos junto com o código: conversor gerado/equivalência, net48, contratos de
  origem (Fase 13 pela API), runtime offline por pacote, conector de assinatura. Coberturas
  novas:
  - `Phase22GateTests` (MongoDB real): cadastro, conversão, 422/404, versões, conteúdo da
    versão, republicação do conteúdo aberto, rollback, permissões e isolamento;
  - testes de `openVersion.js` (`node --test`);
  - e2e no navegador reescrito para o fluxo novo.

## Alternativas rejeitadas

- **Manter o SDK como opção**: o usuário pediu expressamente que não haja DLL separada.
- **Versionar o DE-PARA em arquivos no Git**: o usuário pediu que o próprio serviço versione.
  Os snapshots imutáveis já cumprem isso, com auditoria e rollback.
- **Remover do motor a leitura de extensões de origem**: quebraria snapshots já publicados
  (ex.: o template do ERP da Fase 21).
