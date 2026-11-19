# Guia do consumidor — SDK e API

Referência completa de contratos: [README.md](README.md). Este guia mostra o
caminho feliz de quem integra a plataforma.

## Transformar XML de origem em XML de destino (fluxo principal)

O template publicado com **XSD de origem** (ADR-0015) aceita o XML no
formato desse XSD e devolve o XML do XSD de destino:

```http
POST /v1/organizations/org-a/templates/dps-erp/operations/emissao/transform?correlationId=pedido-8812
Authorization: ApiKey tradux_...        (credencial com papel runtime)
Content-Type: application/xml

<?xml version="1.0" encoding="ISO-8859-1"?>
<NotaErp xmlns="..." versao="2.1" situacao="1"> ... </NotaErp>
```

- O XML é validado contra o XSD de origem **antes** do mapeamento; fora do
  XSD → `200` com `success: false` e `SOURCE_XML_INVALID` (linha e coluna).
  A codificação declarada no XML é respeitada.
- Mesma resposta de `POST …/xml` (`success`, `xml`, `diagnostics`,
  `versionUsed`, `versionsAttempted`). Versão fixa: `?version=3`; fallback:
  `?versions=3,2` (cada versão valida com o próprio XSD de origem).
- Template sem XSD de origem: `TEMPLATE_WITHOUT_SOURCE_SCHEMA` — use `…/xml`
  com JSON, ou republique com o XSD de origem.
- Corpo até 1 MiB (`413` acima); profundidade/nós/textos com os limites da
  ADR-0014 (`400`).
- SDK: `runtime.TransformXmlAsync(new TransformXmlRequest("org-a", "dps-erp", "emissao", xml))`
  — também no `TraduxPackageRuntime` offline.

No mapeamento, os campos de origem vêm do XSD de origem: caminhos com ponto
(`Servico.Codigo`), atributos como `@nome` (`@situacao`), texto de elemento
com atributos como `$value` (`Servico.Valor.$value`), coleções pelo
`maxOccurs` do XSD (bloco "for each" do editor).

### Layout próprio dentro de um XSD oficial (contrato de entrada)

Se o XML do ERP traz blocos que o XSD oficial não prevê (ex.: `<infErp>`
dentro da `<DPS>`), monte no editor o **contrato de entrada**: carregue os
XSDs oficiais, use "+ bloco"/"+ campo" na árvore e salve uma versão
(ADR-0016). O template usa essa versão; o XML com o bloco é aceito por
`…/transform` e validado (blocos próprios pelas regras do contrato, o resto
pelo XSD oficial).

### Nova versão do XSD da prefeitura

Antes de trocar o XSD de destino de um template publicado, envie o XSD novo
para `POST …/templates/{id}/operations/{op}/schema-changes`. A resposta
diz:
- o que remapear, com sugestões;
- o que mapear;
- o que revisar: tipo, restrições, cardinalidade;
- o que é só informativo, como a sequência de tags (o motor já acompanha).

No editor, basta carregar o XSD novo sobre o rascunho: o painel "Mudanças do
XSD" mostra a mesma lista. Nenhum mapeamento some sem aviso e a publicação
espera até cada pendente ser movido ou descartado (ADR-0017).

### Conversor C# gerado e assinatura

`tradux-codegen` (ou `GET …/codegen/csharp`) gera o conversor do template, com:
- entrada e saída tipadas;
- constantes das tags;
- eventos antes/depois de cada tag;
- checagem de versão;
- as regras do DE-PARA em C# legível, rodando sem banco nem API (ADR-0021).

Rode no build para o código acompanhar o template. Para assinar (DPS por tag,
ou o documento inteiro), use o conector `Tradux.Connectors.XmlSignature` no
evento `XmlGenerated`. Com a chave fora do processo (HSM, token A3, nuvem),
passe `SigningKey` (ex.: `DelegatedRsa`). Para conferir o que foi assinado, use
`XmlSigner.Inspect`. Há um exemplo completo em
`<pasta-dev>\exemplos\dps-erp-webservice` (ADR-0018/0019).

## Gerar XML pela API (entrada em JSON)

```http
POST /v1/organizations/org-a/templates/dps/operations/emissao/xml
Authorization: ApiKey tradux_...        (credencial com papel runtime)
Content-Type: application/json

{ "data": { "Numero": "000123", "Serie": "A1", ... }, "correlationId": "pedido-8812" }
```

Resposta `200` mesmo quando a geração falha por regra ou XSD: confira
`success`, `xml` e `diagnostics` (código estável, mensagem, XPath de destino,
sugestão). `404` = não há versão publicada (ou organização errada). `400` =
pedido malformado (`data` não é objeto, `correlationId` fora do formato,
mais de 5 versões no fallback).

- Versão fixa: `"version": 3`. Fallback: `"versions": [3, 2]` — a primeira
  que gera XML válido vence; `versionsAttempted` traz os erros de cada uma.
- `correlationId`: identificador opaco (até 64 caracteres
  `[A-Za-z0-9._:-]`), vai para logs e traces — nunca use dado fiscal.

## Gerar XML pelo SDK (em processo)

```csharp
RegexTimeout.Configure();   // 1ª linha do processo (proteção contra pattern patológico)
var runtime = new TraduxRuntime(new MongoTemplateSnapshotRepository(client, options));
var result = await runtime.GenerateXmlAsync("org-a", "dps", "emissao", new DpsInput { Numero = "000123", ... });
```

O SDK aceita objeto C# ou JSON e produz o mesmo resultado que a API.
Mantenha **uma** instância por processo (ela é o cache) e rode
`RunCacheInvalidationAsync` para enxergar publicações de outros processos.

Sem banco: `TraduxPackageRuntime.Load(bytesDoPacote)` gera a partir de um
pacote portátil (hash conferido ao carregar).

## Publicar

Toda publicação passa por compilação, testes estruturais automáticos e pelo
menos um cenário funcional com XML esperado ou asserções:

- Editor visual: painel "5. Publicar" (dados de exemplo + XML esperado).
- API: `POST …/publications` (conteúdo) ou camadas (`POST /v1/organizations/{org}/layers`,
  `PUT …/binding`) — permissão `template.publish`.
- SDK: `TraduxRuntime.PublishAsync(..., scenarios, actor)` (exige
  `auditSink`) ou `TraduxAdministration`.

Recusa: `422` (API) ou `PublicationRejectedException` (SDK), com
diagnósticos e relatório; a versão ativa não muda.

## Testar antes de publicar

- `POST /v1/design/test` — gera com dados de exemplo e compara com o XML
  esperado (comparação semântica).
- `POST /v1/design/quality` — suíte estrutural automática (obrigatórios,
  limites, enumerações, repetições, nulos, inválidos).

## Promover entre ambientes

`GET …/package` no ambiente de origem e `POST /v1/organizations/{org}/packages?templateId=&operation=`
no destino — ver `docs/runbooks/PROMOCAO_POR_PACOTE.md`.

## Buscar documentação

`GET /v1/knowledge/search?q=como fazer rollback` (permissão `template.read`):
trechos com `sourceId`, `version` e `anchor` da fonte. É apoio — nunca regra
executável.

## Permissões

| Papel | Pode |
|---|---|
| `runtime` | gerar XML |
| `editor` | ler, criar, editar, testar |
| `auditor` | ler templates e auditoria |
| `admin` | tudo, inclusive publicar, importar/exportar e captura diagnóstica |
