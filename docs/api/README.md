# API do microsserviço tradutor de XML

> **Fase 22 (ADR-0025): o produto é só o microsserviço.** O SDK, o conversor C# gerado e
> o conector de assinatura foram removidos; as seções abaixo que os citam são histórico.
> Comece pela seção "Uso do microsserviço".

## Uso do microsserviço (Fase 22)

Toda chamada leva `Authorization: ApiKey <credencial>` (ou `X-Api-Key`). A organização é a
da credencial; a operação é `default` (ou `?operation=`).

| Rota | Permissão | O que faz |
|---|---|---|
| `GET /v1/templates` | `template.read` | Templates com a versão ativa. |
| `PUT /v1/templates/{nome}` | `template.publish` | Cadastra a próxima versão. 200 = `snapshotVersion` ativa; 422 = gates recusaram (a ativa não muda); 400 = nome inválido; 413 = corpo acima de `Tradux:Design:MaxRequestBytes` (padrão 28 MiB). |
| `POST /v1/templates/{nome}/convert` | `template.use` | Corpo: XML de origem. 200 = XML de destino (`application/xml`, cabeçalhos `X-Template-Version`, `X-Correlation-Id`); 422 = XML fora do XSD de origem ou regra que falhou (JSON com `diagnostics`); 404 = sem versão. Query: `version`, `versions` (fallback, ex. `3,2`), `correlationId`, `format=json`. Corpo até `Tradux:Generation:MaxRequestBytes` (padrão 1 MiB; 413 acima). |
| `GET /v1/templates/{nome}/versions` | `template.read` | Histórico (mais nova primeiro). |
| `GET /v1/templates/{nome}/versions/{v}` | `template.read` | Conteúdo da versão (XSDs, mapeamento, tabelas, cenários) — o que o editor abre. |
| `POST /v1/templates/{nome}/rollback` | `template.publish` | Corpo `{ "version": n }` (omitido = anterior à ativa). 200 / 409. |

Corpo do `PUT` (o mesmo que o editor envia):

```json
{
  "schema":       { "files": [{ "name": "destino.xsd", "contentBase64": "…" }], "entryPoint": "destino.xsd" },
  "sourceSchema": { "files": [{ "name": "origem.xsd",  "contentBase64": "…" }], "entryPoint": "origem.xsd" },
  "mapping":      { "fields": [ … ], "complex": [ … ], "collections": [ … ] },
  "lookupTables": { "tabela": { "de": "para" } },
  "scenarios":    [{ "name": "exemplo", "sourceXml": "<…/>", "expectedXml": "<…/>" }]
}
```

Converter:

```http
POST /v1/templates/dps-municipio/convert
Authorization: ApiKey tradux_…
Content-Type: application/xml

<?xml version="1.0" encoding="UTF-8"?><NotaErp …>…</NotaErp>
```

Versões são snapshots imutáveis no MongoDB; publicar/reativar vale na hora na instância que
atendeu e, nas outras, pelo Change Stream (ADR-0005). A assinatura XML fica com quem chama.

Limites de corpo (por rota, ADR-0025 §8): as rotas que recebem XSD em Base64 — `/v1/design/*`,
`PUT /v1/templates/{nome}`, `…/publications`, `…/schema-changes` e `POST …/layers` — usam
`Tradux:Design:MaxRequestBytes`; `…/xml`, `…/transform` e `…/convert`, `Tradux:Generation:MaxRequestBytes`;
`POST …/packages`, 40 MiB. Acima do limite: `413` (`application/problem+json`).

Campos que saíram do JSON (2026-11-17, nenhum cliente os usava): `sourceExtensions` no teste de
rascunho, `extension` nos nós de `SourceFieldTree`, `passedCount`/`failedCount` no relatório de
qualidade (conte `report.cases`). A recusa de aprovação/rejeição de atualização (`409`) passou a
ser `application/problem+json` (mesmos `title` e `detail`).

## Histórico: contratos preliminares (Fases 1–21)

Status: seções 0–7 são o esboço conceitual da Fase 1 (continuam valendo
como intenção). **O que já está implementado — rotas, tipos C# e
diferenças em relação ao esboço — está na seção 8 (Fase 5).** O contrato
OpenAPI vivo é servido pela própria API em `/openapi/v1.json`. Mantido pelo
agente `api-sdk`.

Fonte normativa: seções 10, 18, 20 e 26 de
`docs/requirements/PROMPT_MESTRE_CLAUDE.md`.

## 0. Princípio inegociável

API e SDK são **duas fachadas sobre o mesmo motor** (`Tradux.Engine`).
Nenhuma regra de geração, validação ou resolução de camadas/versões vive na
API ou no SDK — ambos apenas: (a) recebem a chamada no formato do seu
protocolo, (b) normalizam a entrada para a árvore interna comum, (c) invocam
o motor, (d) devolvem o resultado no formato do seu protocolo. Qualquer
divergência de comportamento entre uma chamada via API e a chamada
equivalente via SDK é um defeito, não uma variação aceitável.

## 1. Convergência de entrada (seção 10) — antes do motor, não dentro dele

```text
API   → instância JSON ------------------\
                                           > Normalizador de contrato de origem →
SDK   → instância JSON ou objeto C# ------/     árvore interna tipada única  → Tradux.Engine
```

Pontos fixos:

- A API só aceita JSON (instâncias de dados) na entrada.
- O SDK aceita JSON **ou** objetos C# fortemente tipados gerados/associados
  ao contrato de origem.
- Os dois caminhos convergem para a **mesma árvore interna tipada** antes de
  qualquer chamada ao motor. A normalização (parsing de JSON, binding de
  objeto C#, validação estrutural contra o contrato de origem) é lógica de
  contrato/motor, não de camada de transporte — deve residir junto do motor
  ou em uma camada de contratos compartilhada por API e SDK, nunca duplicada
  separadamente em cada projeto.
- Consequência de design: o motor nunca deveria saber se a chamada veio de
  HTTP ou de uma chamada de método C# direta. Ele só enxerga a árvore
  interna, a operação, a organização/template e o contexto.

## 2. Fachadas mínimas (seção 26)

Convenções da tabela: nomes são lógicos (identificam a operação, não uma
assinatura C# ou rota HTTP definitiva). "Permissão exigida" referencia a
lista fechada da seção 20 — ver `CLAUDE.md`/seção 20 do requisito de origem;
autenticação/autorização em si é escopo do agente `security-observability`
(Fase 9), aqui só se declara a pré-condição.

| # | Fachada | Parâmetros (conceituais) | Retorno (conceitual) | Permissão exigida |
|---|---|---|---|---|
| 1 | Importar contrato de origem | organizacaoId, arquivoOuPacote (XSD único ou ZIP), tipoContrato ("xsd" \| "modeloObjeto"), opções de importação (limites de segurança, seção 11) | ContratoImportado { contratoId, módulos[nome/namespace, versão, hash], avisosImportacao[], duracaoMs } | `template.create` (assunção — ver seção 5, item A) |
| 2 | Criar template e versão | organizacaoId, contratoId, schemaDestinoId, nomeTemplate, baseadoEmVersaoId? | TemplateCriado { templateId, versaoId, criadoEmUtc } | `template.create` |
| 3 | Criar camada (produto ou cliente) | organizacaoId, templateId/versaoBaseId, tipoCamada ("produto" \| "cliente"), mapeamentosDelta | CamadaCriada { camadaId, tipoCamada, diferencasRegistradas[] } | `template.edit` |
| 4 | Validar rascunho | organizacaoId, templateId, versaoRascunhoId (ou camadaId) | RelatorioValidacao (ver seção 3), obrigatoriosSemMapeamento[], errosTipoCardinalidade[] | `template.edit` (assunção — ver seção 5, item B) |
| 5 | Executar testes | organizacaoId, templateId, versaoId, conjuntoTestes? (estruturais e/ou funcionais, seção 19) | RelatorioTestes { totalCasos, casosFalhos[], obrigatorioParaPublicarSatisfeito: bool } | `template.test` |
| 6 | Publicar | organizacaoId, templateId, versaoId, correlacaoId? | Publicacao { publicacaoId, versaoPublicadaId, snapshotHash, publicadaEmUtc } | `template.publish` |
| 7 | Rollback | organizacaoId, templateId, publicacaoAlvoId (ou "anterior") | RollbackResultado { versaoAtivaId, revertidoDeId, revertidoEmUtc } | `template.publish` (assunção — ver seção 5, item C) |
| 8 | Gerar XML | ver contrato detalhado na seção 3 abaixo | ResultadoGeracao (ver seção 3) | `template.use` |
| 9 | Listar e comparar versões | organizacaoId, templateId, versaoAId?, versaoBId? (comparação só quando ambas presentes) | ListaVersoes[] \| DiferencaVersoes { origemPorRegra[] } | `template.read` |
| 10 | Importar pacote | organizacaoId, pacotePortavel (manifesto + contratos + XSDs + mapas + regras, seção 22) | PacoteImportado { relatorioIntegridade, hashesPreservados: bool } | `template.import` |
| 11 | Exportar pacote | organizacaoId, templateId, versaoId (ou publicacaoId) | PacotePortavel (stream/arquivo) + manifesto | `template.export` |

Transversal a todas as fachadas: qualquer chamada pode opcionalmente pedir
captura diagnóstica detalhada (payloads intermediários, árvore interna,
trace de regras aplicadas). Isso exige adicionalmente `diagnostic.capture` —
nunca é concedido implicitamente por outra permissão. Consultar o histórico
de qualquer uma dessas ações (quem, quando, o quê) exige `audit.read` e é
uma fachada de leitura separada (não listada acima porque não gera nem
altera templates), mas deve existir em ambos os formatos pelo mesmo
princípio de equivalência.

## 3. Fachada de geração de XML (detalhe obrigatório da seção 26)

Assinatura conceitual (idêntica em espírito entre API e SDK; API recebe tudo
serializado em JSON no corpo da requisição, SDK aceita o mesmo shape como
objeto C# ou como JSON):

```text
GerarXml(
  organizacaoId: string,                 // obrigatório
  templateId: string,                    // obrigatório
  operacao: string,                      // obrigatório; conjunto extensível
                                          // (ex.: "emissao", "consulta",
                                          // "cancelamento",
                                          // "cancelamentoPorSubstituicao",
                                          // ou operação customizada — seção 9)
  dados: <JSON | objeto C#>,             // obrigatório; instância do
                                          // contrato de origem do template
  contexto?: mapa chave-valor,           // opcional; dados auxiliares que
                                          // não pertencem ao contrato de
                                          // origem (ex.: flags de ambiente)
  politicaVersao?: PoliticaVersaoSelecao,// opcional; default = versão ativa
                                          // publicada; pode fixar uma versão
                                          // específica ou uma lista ordenada
                                          // de candidatas (seção 17)
  correlacaoId?: string                  // opcional; ecoado no relatório e
                                          // usado em auditoria/observabilidade
) -> ResultadoGeracao
```

`ResultadoGeracao` (retorno):

| Campo | Tipo | Obrigatório | Descrição |
|---|---|---|---|
| `sucesso` | bool | sim | Indica se alguma versão candidata produziu XML válido. |
| `xml` | string \| null | sim (presente sempre; valor `null` se `sucesso = false`) | XML final, já validado contra o schema exato da versão usada. |
| `versaoUsada` | string \| null | sim | Identificador da versão de template que produziu o resultado; `null` se todas falharem. |
| `relatorio` | RelatorioResultado (seção 4) | sim | Relatório estruturado completo da tentativa (ou tentativas, em caso de fallback). |
| `avisos` | Ocorrencia[] (seção 4) | sim (pode ser vazio) | Subconjunto de `relatorio` filtrado por nível "aviso" — conveniência para quem não quer processar o relatório inteiro. |
| `metadados` | mapa chave-valor extensível | sim (pode ser vazio) | Dados adicionais não normativos: hash do snapshot usado, timestamp UTC, templateId/organizacaoId ecoados, etc. |

## 4. Relatório estruturado de resultado (contrato de dados, seção 18)

Este é um contrato de dados compartilhado — usado por `GerarXml`, `Validar
rascunho` e `Executar testes` — e por isso não pode divergir entre API e
SDK; é serializado como JSON pela API e como o mesmo shape (JSON ou tipo C#
equivalente) pelo SDK.

**`RelatorioResultado`**

| Campo | Tipo | Obrigatório | Descrição |
|---|---|---|---|
| `sucesso` | bool | sim | Resultado final agregado. |
| `xml` | string \| null | sim | Presente somente quando `sucesso = true`. |
| `versaoUsada` | string \| null | sim | Versão que produziu o resultado final, `null` se nenhuma. |
| `versoesTentadas` | `TentativaVersao[]` | sim (mínimo 1 item) | Ordem de fallback percorrida (seção 17). |
| `ocorrencias` | `Ocorrencia[]` | sim (pode ser vazio) | Achados agregados da versão usada, ou de todas as tentativas se todas falharem. |
| `duracaoMs` | number | sim | Duração total da operação, do recebimento da chamada ao retorno. |
| `correlacaoId` | string \| null | não | Ecoa o valor de entrada; se ausente na entrada, o motor pode gerar um e devolvê-lo aqui. |

**`TentativaVersao`**

| Campo | Tipo | Obrigatório | Descrição |
|---|---|---|---|
| `versao` | string | sim | Identificador da versão candidata tentada. |
| `sucesso` | bool | sim | Se essa tentativa específica produziu XML válido. |
| `ocorrencias` | `Ocorrencia[]` | sim (pode ser vazio) | Achados específicos dessa tentativa. |
| `duracaoMs` | number | sim | Duração isolada dessa tentativa. |

**`Ocorrencia`**

| Campo | Tipo | Obrigatório | Descrição |
|---|---|---|---|
| `nivel` | enum (`informacao` \| `aviso` \| `critico`) | sim | Seção 18. Falhas de XSD, conversão, cardinalidade, compilação e execução são sempre `critico`. Validações de negócio podem ter severidade configurável quando o operador/regra permitir. |
| `codigo` | string | sim | Código estável, namespaced (ex.: `ENGINE.CARDINALITY_VIOLATION`, `IMPORT.XSD_EXTERNAL_REF_BLOCKED`). Nunca muda entre versões sem processo de depreciação formal — é o único campo que integrações devem usar para lógica condicional. |
| `mensagem` | string | sim | Texto legível por humanos, não destinado a parsing. |
| `origem` | string | sim | Componente/etapa que gerou a ocorrência (ex.: `importacao-contrato`, `validacao-xsd`, `motor-regras`, `resolucao-camadas`). |
| `xpathDestino` | string \| null | não | Presente quando a ocorrência está associada a um nó específico do XML de destino. |
| `regraResponsavel` | string \| null | não | Identificador do mapeamento/operador/regra que originou a ocorrência, quando aplicável. |
| `sugestao` | string \| null | não | Texto de correção acionável, quando o motor consegue inferir um. |

Notas de compatibilidade: novos campos opcionais podem ser adicionados a
qualquer um destes contratos sem quebra (consumidores devem ignorar campos
desconhecidos). Remover ou renomear um campo, ou mudar o tipo/semântica de
`codigo`, é uma mudança incompatível e segue a estratégia de versionamento
de contrato a ser definida no ADR de versionamento (Fase 1, ainda pendente).

## 5. Ambiguidades resolvidas por suposição nesta fase

A seção 20 do requisito de origem lista permissões fechadas, mas nem toda
fachada da seção 26 tem uma permissão 1:1 óbvia. Resolvido por suposição,
a confirmar pelo agente `security-observability` na Fase 9:

- **A.** "Importar contrato de origem" não tem permissão própria na lista da
  seção 20. Assumido como pré-condição de `template.create`, já que importar
  um contrato é sempre o primeiro passo para criar um template — não é uma
  ação isolada com valor próprio sem um template subsequente.
- **B.** "Validar rascunho" (checagem estrutural/semântica contínua durante
  edição, seção 12) é distinta de "Executar testes" (execução formal dos
  casos de teste, gate de publicação, seção 19). Assumido que validar
  rascunho exige `template.edit` (é parte do ciclo de edição) e executar
  testes exige `template.test` (ação distinta e auditável separadamente).
- **C.** "Rollback" não tem permissão própria. Assumido como pré-condição de
  `template.publish`, por ser uma ação que também altera qual versão está
  ativa em produção — mesma classe de risco que publicar.

Essas três suposições são reversíveis sem custo estrutural: são apenas
mapeamentos de fachada→permissão, não a implementação de autorização em si.

## 6. Fora de escopo deste documento

- Especificação OpenAPI (YAML/JSON) completa — depende do ADR de versão
  .NET/ASP.NET Core, ainda não fechado.
- Assinaturas C# reais do SDK (nomes de classes, namespaces, overloads).
- Implementação de autenticação/autorização — agente `security-observability`,
  Fase 9.
- Formato de serialização de erros de transporte (HTTP status codes,
  envelope de erro de infraestrutura) — parte da especificação OpenAPI da
  Fase 5.

## 7. Próximos passos (Fase 5)

Transformar cada fachada da seção 2 em: (a) uma rota HTTP versionada com
schema JSON de request/response derivado dos contratos de dados desta
página; (b) um método C# público no SDK com o mesmo shape de
entrada/saída; (c) um teste de equivalência automatizado que chama as duas
fachadas com a mesma entrada e compara o `RelatorioResultado` resultante.

## 8. Implementado na Fase 5

Decisões: ADR-0008. Nomes do contrato implementado estão em inglês (código
C#/JSON); a tabela abaixo liga cada campo ao nome conceitual das seções 3–4.

### 8.1 Fachadas disponíveis

| Fachada (seção 2) | API HTTP v1 | SDK (`Tradux.Sdk.TraduxRuntime`) |
|---|---|---|
| 8 — Gerar XML | `POST /v1/organizations/{organizationId}/templates/{templateId}/operations/{operation}/xml` com corpo `{ "data": {…}, "version"?: n, "correlationId"?: "…" }` | `GenerateXmlAsync(GenerateXmlRequest)` (dados JSON) e `GenerateXmlAsync(org, template, operation, object data, version?, correlationId?)` (objeto C#) |
| 9 — Listar versões | `GET …/operations/{operation}/versions` | `ListVersionsAsync(org, template, operation)` |
| 6 — Publicar | — (não exposta por HTTP até a Fase 9) | `PublishAsync(org, template, operation, SnapshotContent, cenários, actor)` — desde a Fase 8 passa pelos gates (ver §11) |

`TraduxClient` (Fase 2) continua existindo para compilar/gerar em
memória sem snapshot publicado.

### 8.2 Correspondência de campos (`GenerationResult`)

| Esboço (§3–4) | Implementado | Observação |
|---|---|---|
| `sucesso` | `success` | |
| `xml` | `xml` | `null` quando `success=false`. |
| `versaoUsada` | `versionUsed` | Número da versão do snapshot como texto (`"3"`); `null` se falhou. |
| `versoesTentadas` | `versionsAttempted[]` (`version`, `success`, `diagnostics`, `durationMs`) | Fase 5: sempre 0 ou 1 tentativa (0 quando não há snapshot sem versão fixada). |
| `ocorrencias` | `diagnostics[]` (`severity`, `code`, `message`, `destinationXPath`, `sourcePath`, `suggestion`) | `severity` é texto: `Info`/`Warning`/`Critical`. `origem` e `regraResponsavel` ainda não existem (Fase 9); `sourcePath` descreve a regra de origem. |
| `avisos` | — no JSON | Propriedade de conveniência `Warnings` só no tipo C#. |
| `duracaoMs` | `durationMs` | Número (ms). |
| `correlacaoId` | `correlationId` | Ecoado; gerado quando ausente. |
| `metadados` | `metadata` | `organizationId`, `templateId`, `operation`, `snapshotVersion`, `snapshotContentHash`. |
| `contexto` (entrada) | — | Ainda não suportado. |
| `politicaVersao` (entrada) | `version` | Só "ativa" (omitido) ou "fixada"; lista de candidatas/fallback na Fase 7. |

### 8.3 Status HTTP

- **200** — a geração executou; `success` diz se o XML foi produzido e
  validado (falha de regra ou de XSD também é 200, com diagnósticos).
- **404** — não há snapshot para a chave/versão; corpo é o mesmo
  `GenerationResult` com o código `TEMPLATE_SNAPSHOT_NOT_FOUND`.
- **400** — pedido malformado (`application/problem+json`), ex.: `data`
  ausente ou não é objeto JSON, número fora da faixa de `decimal`.

### 8.4 Dados de entrada (`data`)

Objeto JSON com os **mesmos nomes** do contrato de origem que um objeto C#
teria (sensível a maiúsculas/minúsculas: `Numero`, não `numero`). Números
viram `decimal`; arrays viram coleções iteráveis pelos blocos `for each`;
datas vão como texto ISO-8601.

**Regra de equivalência (ADR-0008 §5):** o SDK converte um objeto C#
passando-o pelo `System.Text.Json` — o mesmo caminho de um JSON recebido
pela API. Logo, o JSON que o `System.Text.Json` produz a partir de um
objeto dá pela API exatamente o mesmo resultado que o objeto dá pelo SDK
(datas com ou sem fuso, `enum` como número, listas, objetos aninhados). JSON
montado à mão continua equivalente em comparações de data, `formatDate` e
emissão de `xs:date`/`xs:gYearMonth`; em operadores de texto (`concat`,
`substring`, `lookup`…) o texto é usado como enviado, então use a forma do
`System.Text.Json` (ex.: `"2026-09-28"`, `"2026-09-28T10:00:00-03:00"`).

Valores que não cabem no tipo do elemento de destino (`"abc"` ou
`"1500,50"` num decimal, objeto ou lista num texto) não derrubam a
chamada: a resposta é 200 com `success=false` e o diagnóstico crítico
`VALUE_CONVERSION_FAILED` apontando o XPath. Decimais em texto usam ponto
e não aceitam separador de milhar. Em coleção de valores escalares, cada
item fica disponível como `$value`.

### 8.5 Equivalência API × SDK

Coberta por `tests/Tradux.Api.IntegrationTests/Phase5GateTests.cs`
contra a API real e MongoDB real, comparando `success` (e o valor esperado
por cenário), XML semanticamente igual, diagnósticos, versão
usada/tentativas, `correlationId` e `metadata`:

- JSON nos dois lados: DPS válido, DPS reprovado no XSD, pedido com
  coleção, DPS com regras e textos sobre datas (inclusive data/hora com
  fuso), template inexistente;
- JSON escrito à mão (API) × objeto C# (SDK): DPS, pedido, DPS com datas;
- `JsonSerializer.Serialize(objeto)` (API) × objeto (SDK): DPS, pedido,
  DPS com datas.

### 8.6 Autorização

Ainda não implementada (Fase 9). Permissões previstas: gerar XML →
`template.use`; listar versões → `template.read`; publicar →
`template.publish`. Até lá a organização vem da rota e não é verificada —
não expor a API fora de rede confiável.

## 9. Fachadas de desenho (Fase 6)

Decisões: ADR-0009. Sem estado — o rascunho não é gravado (desde a Fase 9, teste e qualidade geram só um evento de auditoria `template.test`).
Usadas pelo editor visual (`Tradux.Web`) e expostas também no SDK
(`Tradux.Sdk.TraduxDesigner`), sobre o mesmo
`DraftDesignService`.

| Fachada (seção 2) | API | SDK |
|---|---|---|
| Catálogo de operadores (apoio ao editor) | `GET /v1/design/operators` → `OperatorInfo[]` (nome, categoria, descrição, aridade, resultado, parâmetros com tipo) | `ListOperators()` |
| Importar contrato de destino (sem persistir) | `POST /v1/design/schema` com `{ files: [{ name, contentBase64 }], entryPoint, rootElementName? }` → `DestinationTree` (nós com `path`, `kind`, cardinalidade, `nillable`, `scalarType`, `mappable`) | `DescribeSchema(SchemaUpload)` |
| 4/5 — Validar rascunho / executar teste | `POST /v1/design/test` com `{ schema, mapping, data, lookupTables?, expectedXml? }` → `DraftTestResult` (`publishable`, `compileDiagnostics`, `generation`, `expectedXmlMatches`, `contentHash`) | `TestDraft(DraftTestRequest)` |
| 5 — Testes estruturais automáticos (Fase 8) | `POST /v1/design/quality` com `{ schema, mapping, lookupTables? }` → `DraftQualityResult` (`passed`, `compileDiagnostics`, `report`) | `CheckQuality(DraftQualityRequest)` |

- `mapping` é o `MappingTemplate` em JSON: `fields`/`complex`/`collections`,
  com expressões `{ "$type": "field", "path" }`, `{ "$type": "literal", "value" }`,
  `{ "$type": "operator", "operatorName", "arguments": [...] }`.
- Caminhos de origem aceitam ponto para objetos aninhados
  (`"Tomador.Cnpj"`); dentro de `collections[].itemContent`, os caminhos são
  relativos ao item (`$index` = posição, `$value` = valor de item simples).
- `compileDiagnostics` usa o mesmo XPath dos nós de `DestinationTree`
  (`/DPS/numero`), para o editor marcar o campo; em diagnósticos de
  execução há índice de repetição (`/Pedido/itens/item[1]/descricao`).
- XML esperado: lido sem DTD (`DOCTYPE` recusado); diferença reportada como
  `EXPECTED_XML_MISMATCH` com o XPath e o texto da **primeira** diferença
  (elemento, atributo, texto, elemento a mais ou a menos). Com erro
  crítico de compilação, `generation` vem nulo (os diagnósticos aparecem
  uma vez só, em `compileDiagnostics`).
- Árvore de destino: grupo sem elemento próprio tem caminho próprio
  (`/R/(choice#1)`); conteúdo com `xs:choice`/`xs:all` ou grupo fica todo
  `mappable=false`, como o compilador exige.
- Status: 200 com o resultado (inclusive rascunho inválido); 413 acima de
  `Tradux:Design:MaxRequestBytes` (padrão 28 MB); 400
  (`application/problem+json`) para XSD que não importa, Base64 inválido,
  arquivo sem nome/conteúdo, `entryPoint` ausente,
  corpo sem `schema`/`mapping` ou dados que não são objeto JSON.
- Autorização prevista (Fase 9): operadores/schema → `template.create`/
  `template.edit`; teste → `template.test`.

## 10. Camadas, publicação e atualização (Fase 7)

Decisões: ADR-0010. Escrita pelo SDK (`Tradux.Sdk.TraduxAdministration`,
com repositórios injetados — ex.: adaptadores MongoDB); por HTTP, só leitura
até a autorização da Fase 9.

| Fachada (seção 2) | SDK | API |
|---|---|---|
| 3 — Criar camada (base/produto/cliente) | `CreateLayerVersionAsync(NewLayerVersion, actor)` → camada + atualizações disparadas | — (Fase 9) |
| 6 — Publicar | `BindAndPublishAsync(org, template, op, topo, política, cenários, actor)`, `PublishAsync(...)` → `PublicationResult` (sucesso, versão, conflitos, diagnósticos) | — (Fase 9) |
| 7 — Rollback | `RollbackAsync(org, template, op, versão?, actor)` | — (Fase 9) |
| 9 — Listar/comparar versões | `ExplainAsync(...)`, `ListUpdatesAsync(...)` (diferenças por caminho) | `GET …/versions/{v}/explanation`, `GET …/updates` |
| Atualização manual | `ApproveUpdateAsync`, `RejectUpdateAsync` | — (Fase 9) |

- **Explicação** (`ResolutionExplanation`): `manifest.layers` (camadas exatas,
  ref + hash) e `manifest.paths[caminho]` = `{ finalSource, history: [{ layer,
  kind, operation, comment }] }`; `finalSource` nulo = regra removida por uma
  camada. O mesmo para `manifest.lookupTables`.
- **Geração com fallback** (seção 17): corpo de `POST …/xml` aceita
  `versions: [3, 2]`; a primeira versão que gera XML válido é usada
  (`versionUsed`); se nenhuma servir, `versionsAttempted` traz os
  diagnósticos de cada uma. No SDK: `GenerateXmlRequest.CandidateVersions`.
- **Atualização** (`TemplateUpdate`): `status` `Proposed|Applied|Failed|Rejected|Superseded`,
  `trigger` (versão de camada que disparou), `changes` (`Added|Removed|Changed`
  com camada de origem antes/depois), `conflicts`, `gateFailures`,
  `appliedSnapshotVersion`. Falha nunca altera a versão ativa.

## 11. Qualidade automatizada e gate de publicação (Fase 8)

Decisões: ADR-0011. Toda publicação pública — SDK direto
(`TraduxRuntime.PublishAsync`) e vínculo por camadas
(`TraduxAdministration`) — passa pelo mesmo pipeline: compilação →
testes estruturais automáticos → cenários funcionais do autor. Qualquer
reprovação recusa a publicação sem tocar na versão ativa e gera evento de
auditoria `template.publish` com `outcome=rejected`.

- **SDK direto** (mudança de contrato): `PublishAsync(org, template, op,
  content, scenarios, actor)`; o `TraduxRuntime` precisa ser criado
  com `auditSink` para publicar (só gerar não precisa). Recusa →
  `PublicationRejectedException` com `Diagnostics` e `Report`.
- **Cenário funcional obrigatório** (`FunctionalScenario`): `name`, `data`
  (JSON), `expectedXml?` e/ou `assertions?` — `[{ path, kind, expected? }]`
  com `kind` `Exists|Absent|Equals|Count` e `path` lógico por nome local
  (`/DPS/numero`, `/DPS/@versao`). Ao menos um cenário com XML esperado ou
  asserções; senão `FUNCTIONAL_SCENARIO_REQUIRED`. Falha: `SCENARIO_FAILED`
  com XPath e sem valores.
- **Testes estruturais** (`QualityReport`): `cases[]` =
  `{ name, category, expectedValid, passed, target?, failureCode?,
  destinationXPath? }` com `category`
  `Required|Complete|LowerBound|UpperBound|Enumeration|Repetition|Null|Invalid`;
  `notCovered[]` = `{ area, path, reason }` (ex.: `choice` ainda não
  mapeável). Caso reprovado vira `STRUCTURAL_TEST_FAILED`; inválido aceito
  aparece com `failureCode=INVALID_CASE_ACCEPTED`. Os dados gerados nunca
  saem da suíte.
- **Atualização de camadas**: `TemplateUpdate.report` (suíte da proposta) e
  `regressions[]` = `{ scenario, changed, destinationXPath }` — mudança de
  saída por cenário funcional entre a versão ativa e a proposta.
- **Decimais**: o valor é emitido sem arredondar (completa zeros até 2
  casas, ou até `fractionDigits` se menor); casas a mais que o XSD permite
  reprovam na validação em vez de serem arredondadas em silêncio.
- Autorização prevista (Fase 9): `POST /v1/design/quality` → `template.test`.

## 12. Segurança, escrita por HTTP e observabilidade (Fase 9)

Decisões: ADR-0012. **Toda rota `/v1` exige credencial** (`401` sem ela);
só `/openapi/v1.json` é anônima.

- **Credencial**: `Authorization: ApiKey <segredo>` (ou `X-Api-Key`).
  Emitida pelo SDK (`CredentialService.IssueAsync`, segredo mostrado uma
  vez) ou por configuração (`Tradux:Security:BootstrapCredentials` com
  `OrganizationId`, `Name`, `SecretSha256` — o hash, nunca o segredo — e
  `Roles`). Cache em memória de 30 s (`Tradux:Security:CredentialCacheTtl`):
  revogação vale na hora na instância que revogou e em até 30 s nas demais.
- **Papéis** → permissões: `admin` (todas), `runtime` (`template.use`),
  `editor` (`template.read|create|edit|test`), `auditor`
  (`template.read`, `audit.read`). Falta de permissão: `403`.
- **Isolamento**: rota de outra organização → `404` sem corpo.

| Rota | Permissão |
|---|---|
| `POST …/xml` | `template.use` |
| `GET …/versions`, `…/versions/{v}/explanation`, `…/updates` | `template.read` |
| `GET /v1/design/operators`, `POST /v1/design/schema` | `template.edit` |
| `POST /v1/design/test`, `POST /v1/design/quality` | `template.test` |
| `POST /v1/organizations/{org}/layers` | `template.edit` (+ `template.create` se a camada é nova) |
| `PUT …/binding`, `POST …/binding/publication`, `POST …/publications`, `POST …/rollback`, `POST …/updates/{id}/approve|reject` | `template.publish` |
| `PATCH …/binding` | `template.edit` |
| `GET /v1/organizations/{org}/audit?limit=` | `audit.read` |
| `POST|DELETE …/diagnostics/capture`, `GET|DELETE …/diagnostics/records` | `diagnostic.capture` |

(`…` = `/v1/organizations/{org}/templates/{template}/operations/{op}`.)

- **Escrita**: o ator é sempre o principal autenticado
  (`credential:<nome>#<id8>`). Camada `shared=true` exige também
  `template.publish`. Camada de outra organização inexistente ou não
  compartilhada: mesma resposta (`LAYER_ACCESS_DENIED`). Publicação recusada pelos gates → `422` com o
  `PublicationResult` (diagnósticos + relatório); rollback sem versão ou
  com ativa alterada no meio → `409`; decidir proposta já decidida → `409`.
  `POST …/publications` = publicação do editor: `{ schema, mapping,
  lookupTables?, scenarios }`.
- **correlationId**: até 64 caracteres `[A-Za-z0-9._:-]` (senão `400`); vai para log e trace — nunca use dado fiscal.
- **Pattern lento**: valor cuja checagem de pattern estoura o tempo → `XSD_VALIDATION_TIMEOUT` (crítico).
- **Gestão de credenciais**: SDK `TraduxAccess` (emitir, listar, revogar).
- **Host do SDK**: chame `Tradux.Xsd.Security.RegexTimeout.Configure()` no início do processo (antes de qualquer `Regex`) — sem isso, um XSD com pattern patológico em `default`/`fixed` pode travar a importação; a geração já é protegida pelo `PatternGuard`.
- **Propagação entre organizações**: versão NÃO compartilhada de camada não gera atualização nem auditoria em vínculos de outra organização (não revela lançamentos privados).
- **Fallback**: no máximo 5 versões candidatas (`Tradux:Generation:MaxCandidateVersions`) → `400` acima.
- **Importação de XSD** recusa pattern com quantificador acima de 1000,
  pattern acima de 1000 caracteres ou quantificador ilimitado sobre grupo
  já quantificado (`(a+)+`) — o validador XSD do .NET não tem timeout.
- **Captura diagnóstica**: `POST …/diagnostics/capture` `{ minutes? }`
  (padrão 30, máx. 1440) liga; enquanto ativa, cada geração grava entrada
  e XML **mascarados** (nomes sensíveis: CPF/CNPJ, e-mail, telefone, nome,
  endereço…) e **criptografados** (AES-GCM, `Tradux:Diagnostics:EncryptionKey`
  — 32 bytes em Base64; sem ela, `503`), apagados sozinhos em 24 h (TTL;
  `Tradux:Diagnostics:Retention`, máx. 72 h), até 500 registros por janela.
  Ligar, ler, apagar e desligar são auditados.
- **Observabilidade**: span `tradux.generate` (organização, template,
  operação, versão usada/tentadas, fallback, resultado, códigos de erro,
  correlação, cache hit), métricas `tradux.generation.count`,
  `tradux.generation.duration`, `tradux.cache.lookups`,
  `tradux.publication.count`, log estruturado por geração (EventId 1001).
  OTLP ligado por `OTEL_EXPORTER_OTLP_ENDPOINT`. Nunca entram dados de
  entrada, XML, mensagens de diagnóstico ou corpo de requisição.
- **Desenvolvimento**: `appsettings.Development.json` traz o hash de
  `tradux_dev-admin-org-a` (admin da org-a) — só para uso local e e2e.

## 13. Pacote portátil e busca de conhecimento (Fase 10)

Decisões: ADR-0013. Guia prático: `docs/api/CONSUMERS.md` e
`docs/runbooks/PROMOCAO_POR_PACOTE.md`.

| Fachada | API | SDK |
|---|---|---|
| Exportar pacote | `GET …/package[?version=]` → `application/zip` (`template.export`) | `TraduxPackages.ExportAsync` |
| Importar pacote | `POST /v1/organizations/{org}/packages?templateId=&operation=` corpo ZIP (`template.import` + `template.publish` + `template.create`) → `PackageImportResult` | `TraduxPackages.ImportAsync` |
| Executar offline | — | `TraduxPackageRuntime.Load(bytes)` → `TraduxRuntime` sem MongoDB |
| Buscar documentação | `GET /v1/knowledge/search?q=&limit=` (`template.read`) → `[{ sourceId, sourceType, version, anchor, effectiveDate, score, snippet, chunkId }]` | — (projeto `Tradux.Knowledge`) |

- **Pacote** (`tradux-package/1`): `manifest.json` (origem, versão e hash do
  snapshot, SHA-256 e tamanho de cada entrada), `snapshot/content.json`,
  `snapshot/manifest.json` (se houver camadas), `snapshot/scenarios.json`
  (os testes), `layers/<org>/<id>/<v>.json`, `binding.json`. JSON canônico e
  ZIP com data fixa: mesmo template → mesmas entradas em qualquer ambiente.
  Camadas de outra organização vão só como referência + hash (nunca o conteúdo).
- **Importação**: `200` com `hashPreserved: true`; `422` se os gates
  recusarem ou houver `PACKAGE_LAYER_CONFLICT` / `PACKAGE_LAYER_INTEGRITY` /
  `PACKAGE_ORGANIZATION_MISMATCH` / `PACKAGE_HASH_NOT_PRESERVED` (conferido antes de gravar); `400` para pacote inválido (inclusive JSON malformado) (hash de
  entrada, entrada não declarada, caminho perigoso, segredo, limite de
  tamanho/quantidade). Exportação com segredo → `422`.
- **Busca**: escopo = organização da credencial; fontes `Restricted` exigem
  `audit.read`; `503` sem `Tradux:Knowledge:CorpusRoot`/`IndexPath`. Só apoio:
  a geração nunca consulta o RAG.

## 14. Limites, falhas de dependência e saúde (Fase 11)

Decisões no ADR-0014; operação em `docs/runbooks/FALHAS_E_RECUPERACAO.md`;
números em `docs/operations/PERFORMANCE.md`.

- **Limites de entrada da geração** — aplicados em `XmlGenerationService`
  (API `POST …/xml`, `TraduxRuntime`, fallback, `TraduxPackageRuntime`),
  em `POST /v1/design/test`/`TraduxDesigner.Test` e em
  `TraduxClient.GenerateXml`; não se aplicam aos cenários dos gates de
  publicação nem a `/v1/design/quality` (dados sintéticos) — ADR-0014 §3:
  profundidade da árvore de origem ≤ 32, até 10.000 nós (objetos, itens e
  valores), textos até 10.000 caracteres (`Tradux:Generation:Limits`). API:
  `400` com o limite violado, sem ecoar o dado; SDK: lança
  `GenerationInputLimitException` (`ArgumentException`, propriedade `Limit`
  = `depth` | `nodes` | `text`), inclusive no fallback. Mudança de contrato:
  pedidos acima disso passavam antes da Fase 11.
- **Corpo de `POST …/xml`** limitado a 1 MiB (`Tradux:Generation:MaxRequestBytes`) → `413`,
  pelo endpoint roteado (vale também para `/XML` e `/xml/`).
- **MongoDB indisponível**: `503` com `Retry-After: 5` (em vez de `500`),
  em até `Tradux:MongoDb:ServerSelectionTimeout` (5 s). Gerações de templates
  aquecidos na instância continuam `200`. O corpo do problema não traz
  detalhe interno (endereço, exceção).
- **Credenciais com o MongoDB fora**: credencial já validada pela instância
  segue aceita por até `Tradux:Security:CredentialStaleGrace` (10 min) além
  do cache; credencial nunca vista → `503`. Numa instância isolada do banco,
  revogação feita em outra instância leva até esse tempo para valer nela.
- **Saúde** (anônimos, fora de `/v1`, só o status em texto):
  `GET /health/live` → `Healthy`; `GET /health/ready` → `Healthy`, ou
  `Degraded` (HTTP 200) com o MongoDB fora — a instância ainda serve o
  tráfego aquecido.
- `TraduxRuntime` (SDK) aceita `GenerationLimits` no construtor; o
  padrão é o mesmo da API.

## 15. Contrato de origem XSD e entrada XML (Fase 12)

Decisões no ADR-0015; guia em `CONSUMERS.md` ("Transformar XML de origem").

- `POST /v1/organizations/{org}/templates/{t}/operations/{op}/transform`
  (`template.use`): corpo = XML de origem (`application/xml`; codificação do
  próprio XML respeitada); query `version`, `versions` (fallback, "3,2"),
  `correlationId`. Resposta = `GenerationResult` (igual a `…/xml`). Códigos:
  `SOURCE_XML_INVALID` (XML malformado, DTD, fora do XSD de origem, raiz
  errada — mensagem com linha/coluna), `TEMPLATE_WITHOUT_SOURCE_SCHEMA`.
  `400`: corpo vazio, `versions` malformado, limites; `404`: sem snapshot;
  `413`: corpo acima de 1 MiB (limite aplicado ao endpoint roteado).
- `POST /v1/design/source-schema` (`template.edit`): mesmo corpo de
  `/v1/design/schema` (`SchemaUpload`); devolve `SourceFieldTree`
  (`rootElementName`, `targetNamespace`, `contentHash`, `root`) com nós
  `{ name, path, type: text|number|date|object|list, required, nillable,
  children?, item? }` — o mesmo formato da árvore inferida de JSON no editor.
- `POST /v1/design/test`: `sourceSchema` + `sourceXml` (em vez de `data`);
  XML fora do XSD volta em `compileDiagnostics` com `SOURCE_XML_INVALID`.
- `POST …/publications` e `POST /v1/organizations/{org}/layers` (só base):
  campo opcional `sourceSchema` (`SchemaUpload`). Cenário funcional:
  `sourceXml` em vez de `data`.
- SDK: `TraduxRuntime.TransformXmlAsync(TransformXmlRequest)`,
  `TraduxDesigner.DescribeSourceSchema`; `FunctionalScenario.SourceXml`;
  `SnapshotContent.SourceSchema`; `NewLayerVersion.SourceSchema`.
- Hash: o XSD de origem entra no hash do snapshot e da camada **só quando
  presente** — hashes existentes não mudam.

## 16. Contrato de entrada salvo, versionado e estendido (Fase 13)

Decisões no ADR-0016. Contrato = XSD base (ex.: DPS 1.01) + extensões
montadas no editor (blocos/campos próprios, ex.: `infErp`), por
organização, com versões imutáveis.

- `GET /v1/organizations/{org}/source-contracts` (`template.read`): última
  versão de cada contrato (resumo, sem arquivos).
- `GET …/source-contracts/{contractId}/versions` (`template.read`): versões,
  da mais nova para a mais antiga.
- `GET …/source-contracts/{contractId}?version=N` (`template.read`): versão
  completa (`schema` em Base64 + `extensions`); sem `version`, a última.
- `POST …/source-contracts` (`template.edit`): `{ contractId, name, schema,
  extensions?, comment? }` → `201` com `{ contractId, version, contentHash,
  created: true }`; conteúdo igual ao da última versão → `200` com
  `created: false` (nenhuma versão nova). `400`: identificador, XSD ou
  extensão inválidos (a mensagem explica: caminho inexistente, nome repetido…).
  Auditado como `source-contract.version.create`. Outra organização → `404`.
- Extensão: `{ parentPath, node }`; `parentPath` = caminho por nomes
  (`""` = raiz; dentro de lista passa pelo nome da lista); `node` =
  `{ name, isObject, valueType: "Text"|"Number"|"Date", required, repeated,
  children? }`.
- `POST /v1/design/source-contract` (`template.edit`): `{ schema, extensions }`
  → árvore combinada (`SourceFieldTree`) com `extension: true` nos nós de
  extensão.
- Teste de rascunho e publicação: `sourceExtensions` (+ na publicação,
  `sourceContract: { contractId, version }` para rastreio). O template guarda
  uma **cópia** (snapshot autocontido); o hash inclui as extensões só quando
  houver.
- `sourceContract` informado na publicação precisa existir na organização e
  ter o mesmo conteúdo enviado (hash); senão `400`.
- XML de entrada com extensões:
  - os elementos de extensão são validados pelas regras do contrato:
    obrigatório, repetível, tipo, sem filho desconhecido, no namespace do pai e
    sem atributos;
  - o restante é validado pelo XSD base;
  - os erros saem como `SOURCE_XML_INVALID` com posição.
- Limites de leitura, com ou sem extensões: profundidade 32 e até 100.000
  elementos, aplicados durante a leitura.
- SDK: `TraduxSourceContracts` (`InMemorySourceContractRepository` ou
  `MongoSourceContractRepository`); `TraduxDesigner.DescribeSourceContract`.

## 17. Mudanças do XSD de destino (Fase 14)

Decisões no ADR-0017. Nada é gravado.

- `POST /v1/design/schema-changes` (`template.edit`): `{ before, after,
  mapping? }`. `before` e `after` usam o mesmo formato de upload de
  `/v1/design/schema`; `mapping` é o `MappingTemplate` atual. A resposta é
  um `SchemaChangeReport`. `400` quando o pedido está incompleto ou um XSD
  não importa; a mensagem diz qual ("XSD atual: …" / "XSD novo: …").
- `POST /v1/organizations/{org}/templates/{id}/operations/{op}/schema-changes`
  (`template.read`): `{ after }` comparado com o XSD e o mapeamento da
  versão ativa. `404` quando não há versão ativa ou a organização é outra.
- `SchemaChangeReport`: `{ beforeRootElementName, afterRootElementName,
  beforeContentHash, afterContentHash, identical, summary: { remap, map, review,
  info }, changes: [...] }`. Cada item de `changes` traz:
  - `path`: caminho no XSD novo, ou no antigo se o elemento saiu;
  - `kind`: `Added`, `Removed`, `Moved`, `KindChanged`, `TypeChanged`,
    `BecameRequired`, `BecameOptional`, `BecameRepeatable`,
    `StoppedRepeating`, `NillableChanged`, `RestrictionChanged`,
    `DefaultChanged`, `OrderChanged`, `ContentModelChanged`,
    `RootChanged` ou `NamespaceChanged`;
  - `impact`: `Remap`, `Map`, `Review` ou `Info`;
  - `mapped`, `message`, `before`, `after`, `beforePath`;
  - `suggestions`: até 3 caminhos do XSD novo.

  A lista vem ordenada por impacto e depois por caminho.
- A mudança de sequência de tags é `Info`: o XML já sai na ordem do XSD novo.
- SDK: `TraduxDesigner.CompareSchemas` e
  `TraduxRuntime.CompareSchemaWithActiveAsync`.

## 18. Conversor C# gerado, eventos e assinatura (Fase 15)

Decisões nos ADR-0018 e ADR-0019.

- `GET /v1/organizations/{org}/templates/{id}/operations/{op}/codegen/csharp`
  (`template.read`): devolve o arquivo `.cs` do conversor (`text/plain`).
  Parâmetros: `?version=` (padrão: ativa), `?namespace=` e `?prefix=`. `404`
  quando não há a versão.
- O conversor gerado contém `{P}Template` (versão e hash), `{P}Tags`,
  `{P}Entrada`, `{P}Saida` (com `Parse`) e `{P}Converter`
  (`ConvertAsync`/`ConvertXmlAsync`, `Events`, métodos parciais,
  `EnsureCurrentAsync`). Política de versão: `TemplateVersionPolicy`
  (`RequireSameVersion` lança `TemplateCodeOutdatedException`).
- CLI `tradux-codegen` (`tools/Tradux.CodeGen`): `--api --key --org
  --template --operation --out [--namespace --prefix --version --check]`.
- **Eventos** (só no SDK, em C#): `GenerationEvents` com `FieldResolving`,
  `ElementCreated`, `DocumentBuilt` e `XmlGenerated`, passados a uma geração:
  `GenerateXmlAsync(request, events)` / `TransformXmlAsync(request, events)`
  (ADR-0020; não há escopo ambiente).
- **Destino com `xs:choice`**: diagnósticos novos `CHOICE_MULTIPLE_BRANCHES`,
  `CHOICE_NO_BRANCH`, `CHOICE_TOO_MANY` e `MAPPING_MISSING_REQUIRED_CHOICE`.
- Objeto no lugar da lista do "for each" só com `singleObjectAsItem: true` no
  bloco; sem isso é `COLLECTION_SOURCE_NOT_ENUMERABLE`. Campo da raiz dentro de
  um "for each": `{"$type":"field","path":"x","scope":"root"}` (ADR-0020).
- **Assinatura**: pacote externo `Tradux.Connectors.XmlSignature` (`XmlSigner`),
  fora da API.

## 19. Contratos explícitos (Fase 16, ADR-0020)

- `FieldReferenceNode`: `scope` opcional (`"current"`, padrão e não
  serializado; `"root"`). O prefixo `$root.` foi removido: um template com ele
  não compila (`SNAPSHOT_COMPILATION_FAILED` na geração; recusado na publicação).
- `CollectionMapping.singleObjectAsItem` (padrão `false`).
- Relatório de qualidade: categoria nova `Choice` (casos `ramo:<caminho>/<ramo>`,
  um ramo de choice por vez) e código `CHOICE_BRANCH_UNREACHABLE` (ramo de choice
  único que nunca sai sozinho porque outro ramo sempre aparece).
- Conector de assinatura: `SigningKey` (chave do integrador: HSM, A3, nuvem,
  `DelegatedRsa`/`DelegatedEcdsa`), `Inspect` (o que cada assinatura cobre,
  `SignedElements`), chave que não é par do certificado é recusada, Id duplicado
  é recusado. `ICustomSignatureAlgorithm` foi removido.

## 20. Regras em C# no conversor gerado (Fase 17, ADR-0021)

- O `.cs` de `GET …/codegen/csharp` / `tradux-codegen` agora contém:
  - `{P}Regras`: uma regra por destino, em C#, chamando `Ops.*` (operadores
    do motor);
  - `{P}Tabelas`: as tabelas DE-PARA;
  - `{P}Conteudo`: a versão do template embutida.
- `new {P}Converter()` converte sem banco nem API. Para checar a versão
  publicada, use `new {P}Converter(runtime, TemplateVersionPolicy.RequireSameVersion)`.
- Métodos: `ConvertAsync(entrada tipada)`, `ConvertJsonAsync(JsonElement)` e
  `ConvertXmlAsync(xml)` (com XSD de origem).
- SDK: `EmbeddedTemplate` executa um conteúdo embutido com `TemplateCodeRules`.
- Operador customizado: implemente `OperadorCustomizado` (método parcial de
  `{P}Regras`) e passe o catálogo ao conversor.

## 21. Conversor C# pelo editor (Fase 19)

- Painel **Publicar** → seção **Conversor C#**: namespace e prefixo opcionais;
  baixa `{Prefixo}.g.cs` da versão recém-publicada (ou da ativa) via
  `GET …/codegen/csharp` (`template.read`).
- e2e: `node tests/Tradux.Web.Tests/e2e/golden-path.mjs --rascunho <arquivo.json> [--template <id>]`
  importa um rascunho, testa, publica e gera o conversor pela interface.

## 22. Conversor no padrão do ERP (Fase 21, ADR-0024)

- `GET /v1/organizations/{org}/templates/{id}/operations/{op}/codegen/erp` (`template.read`) → ZIP:
  - `Serializacao/{Provedor}/*.cs`: classes do XSD de saída, tipadas como o xsd.exe;
  - `Conversores/{Provedor}ConversorHelper.cs`: C# puro, sem o motor, C# 7.3+:
    `dps.Para{Provedor}(<blocos próprios como parâmetros>)` e `Para{Provedor}Xml`;
  - com `inputModel=true`, também `Entrada/*.cs` (classes de entrada do XSD de origem).
- Query:
  - `version`;
  - `provider`;
  - `namespace` (do helper);
  - `serializationNamespace` (das classes de saída);
  - `inputNamespace` (das classes de entrada que já existem e refletem o XSD de origem, ex.: o da `TCDPS`);
  - `inputRoot` (classe raiz de entrada, ex.: `TCDPS`);
  - `inputModel`.
- 400 quando o template não tem XSD de origem, ou usa um recurso sem tradução
  neste formato (ver ADR-0024).
- CLI: `tradux-codegen --format erp --out <pasta> [--prefix P] [--input-namespace N] [--input-root C] [--input-model] [--check]`.
- SDK: `TraduxRuntime.GenerateErpConverterAsync`.
