# ADR-0013: Pacote portátil, RAG documental e documentação operacional

- **Status**: aceito
- **Data**: 2026-09-30
- **Fase**: 10 — Pacotes, RAG e documentação operacional

## Contexto

Seção 22: pacote importável/exportável (manifesto, contratos de origem,
XSDs e dependências, mapas e regras, camadas, testes, metadados de versão,
hashes) para promoção entre ambientes, backup, execução offline da DLL e
compartilhamento controlado; validar integridade, compatibilidade, limites e
conteúdo; nunca chaves, certificados ou segredos. Seção 24: RAG só para
engenharia e editor, fora da geração determinística; fontes aprovadas e
versionadas; metadados por chunk; isolamento por tenant e nível de acesso;
referência à fonte e versão; reindexação incremental por hash; remoção de
obsoletos; chunking por tipo; avaliação com perguntas/fontes esperadas;
métricas; "comece simples" com armazenamento substituível e ADR antes de
adotar produto. Gate da Fase 10: "um pacote passa entre ambientes com
hashes preservados; o RAG recupera fontes corretas e informa versão, sem
interferir no runtime determinístico". Permissões `template.export` e
`template.import` (seção 20) ficaram para esta fase.

## Decisão

### Pacote (`.traduxpkg`, ZIP)

1. **Unidade**: um template (organização/template/operação) — a versão
   ativa publicada. Conteúdo:
   - `manifest.json`: formato (`tradux-package/1`), origem (organização,
     template, operação), versão e hash do snapshot, data, e a lista de
     todas as entradas com SHA-256 e tamanho;
   - `snapshot/content.json` (XSDs referenciados, mapeamento, tabelas
     DE-PARA, raiz — o `SnapshotContent` canônico) e
     `snapshot/manifest.json` (camadas e proveniência, se houver);
   - `snapshot/scenarios.json`: cenários funcionais com que a versão foi
     publicada (os "testes" do pacote) — o snapshot passa a guardar os
     cenários que o aprovaram;
   - `layers/<org>/<camada>/<versão>.json`: as versões exatas de camada da
     cadeia (com XSD da base);
   - `binding.json`: política de atualização (se vinculado a camadas).
   "Contrato de origem" não existe como artefato persistido (ADR-0009/0011):
   os cenários funcionais são o contrato de fato e vão no pacote.
2. **Hashes preservados**: o conteúdo é determinístico — exportar do
   ambiente A, importar em B e exportar de B produz o mesmo hash por
   entrada e o mesmo hash de snapshot. Para isso a importação grava as
   camadas **na mesma versão** (`ImportVersionAsync`: insere a versão
   exata se não existe; se existe, exige o mesmo `ContentHash`; senão
   conflito) — o hash da camada inclui a referência do pai.
3. **Importação sempre pelos gates**: com camadas → vínculo e publicação
   por `LayeredTemplateService` (resolve a mesma cadeia → mesmo conteúdo e
   manifesto → mesmo hash); sem camadas → `ContentPublicationService`. Os
   cenários do pacote são o teste funcional obrigatório. Depois de
   publicar, o hash do snapshot no destino é comparado com o do manifesto:
   diferença é erro reportado (e auditado).
4. **Validação** (antes de exportar e ao importar): formato suportado;
   entradas sem caminho absoluto, `..` ou barra invertida; limites de
   quantidade/tamanho (os mesmos de importação de XSD, zip bomb incluído);
   todo hash do manifesto confere; nenhuma entrada fora do manifesto;
   **nenhum segredo** — recusa extensões `.pem .key .pfx .p12 .cer .crt
   .jks`, blocos `-----BEGIN`, prefixo de credencial `tradux_` e campos
   chamados senha/password/secret/token/privateKey com valor. Auditoria,
   credenciais e captura diagnóstica nunca entram no pacote.
5. **Organização**: pacote **com camadas** é importado na mesma organização
   de origem (promoção entre ambientes) — trocar a organização mudaria as
   referências e, com elas, os hashes. Pacote **sem camadas** (publicação de
   conteúdo) pode ir para qualquer organização (o hash do conteúdo não
   depende da chave). Camadas de **outra** organização presentes no pacote
   não vão no pacote (só referência + hash no manifesto do snapshot — nunca
   o conteúdo alheio) e precisam já existir no destino com o mesmo hash
   (nunca são criadas em nome de terceiros). Cada camada do pacote tem o
   hash recalculado (integridade contra corrupção, não autenticidade) e o
   hash do snapshot declarado é conferido antes de qualquer gravação; a
   publicação ainda passa pelos gates. Importar exige `template.import` +
   `template.publish` + `template.create`.
6. **Superfícies**: API `GET …/package` (`template.export`, `application/zip`)
   e `POST /v1/organizations/{org}/packages?templateId=&operation=`
   (`template.import`); SDK `TraduxPackages` (exportar/importar com
   repositórios injetados) e `TraduxPackageRuntime` (execução **offline**: o
   pacote vira um repositório em memória, sem MongoDB — mesma geração do
   `TraduxRuntime`). Exportação e importação são auditadas.

### RAG documental

7. **Projeto separado** `Tradux.Knowledge` (sem referência de
   `Domain`/`Engine`/`Application` para ele — teste de arquitetura). O
   runtime de geração nunca consulta o RAG; respostas do RAG não viram
   regra executável (seção 24).
8. **Armazenamento substituível**: porta `IKnowledgeIndex` (upsert,
   remoção, busca). Implementação inicial: índice **lexical BM25** em
   memória persistido em JSON (`artifacts/knowledge/index.json`). Motivo:
   o ambiente não tem serviço de embeddings nem banco vetorial; BM25 é
   determinístico, testável e bom para vocabulário técnico/jurídico exato
   (nomes de operador, códigos, termos fiscais). Vetores entram depois pela
   mesma porta, com novo ADR.
9. **Corpus**: registro de fontes aprovadas em `docs/rag/sources.json`
   (id, caminho, tipo, versão, escopo). Tipos e política de chunking:
   requisito/ADR/runbook/documentação → por seção Markdown (`##`/`###`),
   limite de ~1200 caracteres com sobreposição de 1 parágrafo; glossário →
   por termo; XSD → por declaração de topo (`simpleType`/`complexType`/
   `element`); catálogo de operadores → um chunk por operador (gerado do
   `RuleOperatorMetadata`); API → por seção da referência. Todo chunk leva
   os metadados da seção 24 (`source_id`, `source_type`, `version`,
   `tenant_scope`, `provider`, `municipality`, `operation`,
   `schema_version`, `language`, `content_hash`, `effective_date`) mais
   `access_level` e `anchor` (seção de origem).
10. **Pipeline incremental**: por fonte, compara o hash do conteúdo; fonte
    inalterada não é reprocessada; chunks cujo hash não mudou são mantidos;
    chunks de fonte removida ou que deixaram de existir são apagados. O
    relatório diz adicionados/alterados/removidos/inalterados.
11. **Acesso**: `tenant_scope` (`global` ou organização) e `access_level`
    (`public`, `internal`, `restricted`). A busca recebe o escopo do
    chamador e filtra antes de pontuar. API `GET /v1/knowledge/search?q=`
    exige `template.read` e usa a organização da credencial; cada resultado
    traz `source_id`, `version`, `anchor` e trecho.
12. **Avaliação**: `docs/rag/eval-set.json` com perguntas e fontes
    esperadas; teste calcula precisão@k e cobertura e falha abaixo dos
    limiares registrados em `RETRIEVAL_EVALS.md`.

### Documentação operacional

13. Runbooks em `docs/runbooks/` (instalação/configuração, credenciais,
    rotação de chaves, promoção por pacote, rollback, captura diagnóstica,
    reindexação do RAG, incidentes de cache/MongoDB) e guia do consumidor
    em `docs/api/CONSUMERS.md` (SDK e API), ambos indexados pelo RAG.

## Alternativas consideradas

- **Vetores/embeddings agora**: exigiria serviço externo ou modelo local
  pesado; sem ganho comprovado sobre BM25 para este corpus. Adiado (porta
  pronta).
- **Pacote com todas as versões do histórico**: maior e sem uso na
  promoção; backup do histórico é papel do MongoDB. Um pacote = versão
  ativa; exportar outra versão é parâmetro.
- **Importar reescrevendo camadas de terceiros**: violaria "base e
  produto protegidos" (ADR-0010). Rejeitado — precisam existir no destino.
- **RAG dentro do `Application`**: misturaria apoio com o caminho de
  geração. Rejeitado.

## Consequências

- Snapshot guarda os cenários que o aprovaram (fora do hash).
- Importar exige as mesmas versões de camada: ambientes divergentes geram
  conflito explícito em vez de hash diferente em silêncio.
- A qualidade da busca lexical depende do vocabulário; as avaliações
  documentam o limite.
