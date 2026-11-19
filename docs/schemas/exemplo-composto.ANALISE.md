# Análise — exemplo-composto.xsd (+ exemplo-composto-tipos.xsd)

> **Fixture ilustrativa e sintética**, criada pelo agente `domain-xsd` na
> Fase 1 para servir de alvo à Fase 3 (XSD completo e contratos compostos:
> `include`/`import`, `choice`, coleções, nulabilidade combinada). **Não é**
> um layout oficial de nenhuma prefeitura ou fornecedor. Quando um XSD real
> composto for fornecido, ele deve ser analisado à parte (não substituir
> este arquivo).

Segue o roteiro da skill `xsd-contract-analysis`
(`.claude/skills/xsd-contract-analysis/SKILL.md`).

## 1. Arquivos e namespaces

- `exemplo-composto.xsd` (schema principal, elemento raiz `DpsComposta`).
- `exemplo-composto-tipos.xsd` (tipos compartilhados: `CpfCnpjType`,
  `CodigoServicoType`, `ValorMonetarioType`, `QuantidadeType`,
  `ItemServicoType`), trazido via `xs:include` a partir do schema principal.
- **`xs:include` foi escolhido deliberadamente** (em vez de `xs:import`)
  porque os dois arquivos compartilham o **mesmo** target namespace
  (`http://exemplo.ilustrativo.tradux/dps-composto/v1`) — `xs:include`
  exige mesmo namespace (ou ausência de namespace em ambos); `xs:import`
  seria o caso de borda a exercitar quando um segundo módulo tiver namespace
  *diferente* (ainda não coberto por este par de fixtures; registrar como
  item futuro se a Fase 3 precisar de um caso de `import` cross-namespace).
- Nenhuma referência a schema externo fora deste par de arquivos. Resolução
  de `include` verificada por compilação real via `System.Xml.Schema.
  XmlSchemaSet` (ambos os arquivos juntos compilam sem erro).

## 2. Resumo estrutural

Elemento raiz `DpsComposta` (`DpsCompostaType`), uma única `sequence` no
nível superior contendo: dois escalares obrigatórios, um `choice` real
(tomador), um envelope de coleção repetível (itens), um valor total
obrigatório, um par nillable/opcional contrastante (`valorDeducao` vs.
`dataCancelamento`) e uma observação livre opcional. Sem recursão.

## 3. Tabela de elementos e atributos

| Caminho | Tipo | minOccurs | maxOccurs | nillable | default/fixed | Observação |
|---|---|---|---|---|---|---|
| `DpsComposta` (raiz) | `DpsCompostaType` | 1 | 1 | não | — | elemento raiz |
| `DpsComposta/@versao` | `xs:string` | obrigatório | — | n/a | `fixed="1.00"` | igual ao fixture simples |
| `numero` | `xs:string` | 1 | 1 | não | — | obrigatório |
| `dataEmissao` | `xs:date` | 1 | 1 | não | — | obrigatório |
| `tomador` | complexo (`choice`) | 1 | 1 | não | — | envelope obrigatório do choice |
| `tomador/tomadorPorDocumento` (ramo 1) | complexo | 1\* | 1 | não | — | \*obrigatório **dentro do ramo escolhido**; ver seção 4 |
| `.../tomadorPorDocumento/cpfCnpj` | `CpfCnpjType` (`pattern (\d{11}|\d{14})`) | 1 | 1 | não | — | obrigatório dentro do ramo |
| `.../tomadorPorDocumento/razaoSocial` | `xs:string` | 0 | 1 | não | — | opcional dentro do ramo |
| `tomador/tomadorPorCadastroMunicipal` (ramo 2) | complexo | 1\* | 1 | não | — | alternativa mutuamente exclusiva ao ramo 1 |
| `.../tomadorPorCadastroMunicipal/inscricaoMunicipal` | `xs:string` | 1 | 1 | não | — | obrigatório dentro do ramo |
| `.../tomadorPorCadastroMunicipal/codigoMunicipio` | `xs:string` (`pattern \d{7}`) | 1 | 1 | não | — | obrigatório dentro do ramo |
| `itens` | complexo | 1 | 1 | não | — | envelope obrigatório, não repetível |
| `itens/item` | `ItemServicoType` | 0 | `unbounded` | não | — | coleção repetível verdadeira |
| `.../item/descricao` | `xs:string` | 1 | 1 | não | — | obrigatório por item |
| `.../item/codigoServico` | `CodigoServicoType` | 1 | 1 | não | — | obrigatório por item |
| `.../item/quantidade` | `QuantidadeType` (`xs:decimal`, `minExclusive 0`, 4 casas) | 0 | 1 | não | `default="1"` | opcional por item, com default |
| `.../item/valorUnitario` | `ValorMonetarioType` | 1 | 1 | não | — | obrigatório por item |
| `valorTotal` | `ValorMonetarioType` | 1 | 1 | não | — | obrigatório |
| `valorDeducao` | `ValorMonetarioType` | 1 | 1 | **sim** | — | obrigatório **e** nillable |
| `dataCancelamento` | `xs:date` | 0 | 1 | **não** | — | apenas opcional — contraste com `valorDeducao` |
| `observacao` | `xs:string` | 0 | 1 | não | — | opcional |

\* Dentro de um `xs:choice` com `minOccurs=1 maxOccurs=1` no grupo, cada ramo
tem `minOccurs=1` *relativo à escolha do ramo*: exatamente um dos dois
elementos (`tomadorPorDocumento` OU `tomadorPorCadastroMunicipal`) deve
aparecer, nunca os dois, nunca nenhum.

## 4. Casos de borda

1. **`choice` real de identificação do tomador** — dois ramos
   mutuamente exclusivos e estruturalmente distintos:
   - Ramo 1 (`tomadorPorDocumento`): implica emitir `cpfCnpj` (obrigatório) +
     `razaoSocial` (opcional).
   - Ramo 2 (`tomadorPorCadastroMunicipal`): implica emitir
     `inscricaoMunicipal` + `codigoMunicipio`, ambos obrigatórios.
   O motor de mapeamento precisa decidir o ramo em tempo de geração (via
   regra/condição configurada) e nunca pode emitir os dois ramos nem
   misturar campos de ramos diferentes. Um XML com ambos os ramos presentes,
   ou com nenhum, é inválido.
2. **Coleção repetível com zero ocorrências permitidas** — `item` tem
   `minOccurs=0 maxOccurs=unbounded` dentro do envelope obrigatório `itens`.
   Uma DPS composta com `<itens/>` vazio é **sintaticamente válida**, mas
   semanticamente suspeita (nota fiscal sem nenhum serviço) — tratar como
   aviso de regra de negócio na Fase 4/8, não como erro de schema.
3. **Envelope obrigatório e não repetível ao redor de coleção repetível**
   — `itens` (singular, `minOccurs=1 maxOccurs=1`) não deve ser confundido
   com `item` (plural, repetível). É um padrão comum em layouts fiscais reais
   e deve ser preservado no modelo canônico (nó de coleção != nó do envelope).
4. **Nillable obrigatório vs. apenas opcional (par central deste fixture)**:
   - `valorDeducao`: `minOccurs=1`, `nillable=true` — sempre presente,
     conteúdo pode ser `xsi:nil`.
   - `dataCancelamento`: `minOccurs=0`, `nillable` não declarado (= false) —
     pode faltar inteiramente, mas se presente não aceita `xsi:nil`.
   Esse par existe exatamente para impedir que a implementação do motor
   colapse "opcional" e "aceita nulo" em um único booleano, conforme a regra
   permanente da seção 11 do requisito de origem.
5. **Default em elemento de coleção (`quantidade`)** — `default="1"` só se
   aplica quando o elemento está ausente na instância de entrada antes da
   geração; não deve ser confundido com nulidade nem aplicado quando o valor
   de origem é explicitamente zero.
6. **`include` de tipos compartilhados** — os quatro tipos simples e o tipo
   complexo `ItemServicoType` vêm de um segundo arquivo físico. Um importador
   real deve resolver isso de forma determinística e seguro (ver seção 5).
   Caso de borda relacionado, não coberto por este par de arquivos: `xs:
   import` de um módulo com namespace diferente (candidato a fixture futuro,
   se necessário na Fase 3).

## 5. Nota de segurança (importação de XSD)

- O `xs:include` usa `schemaLocation="exemplo-composto-tipos.xsd"` — caminho
  relativo simples, sem `../` nem URL absoluta. Um importador real deve:
  - Resolver `schemaLocation` apenas dentro do diretório/pacote fornecido,
    rejeitando qualquer caminho que escape do pacote (path traversal) ou que
    aponte para uma URL de rede (SSRF via importação de XSD).
  - Desabilitar `DTD`/entidades externas no parser usado tanto para ler os
    XSDs quanto para validar instâncias XML geradas (risco de XXE).
  - Limitar quantidade de arquivos, profundidade de `include`/`import` e
    tamanho total do pacote, mesmo quando o pacote parece pequeno.
- Nenhum dos dois arquivos deste fixture referencia schema fora do próprio
  par — não há risco de referência externa neste fixture específico, apenas
  a recomendação genérica acima para a implementação do importador (fora do
  escopo deste agente — especialista .NET).

## 6. Fixtures mínimas de dados necessárias (Fase 3 / Fase 8)

| # | Cenário | Descrição |
|---|---|---|
| 1 | Apenas obrigatórios | `numero`, `dataEmissao`, ramo 1 do `tomador` só com `cpfCnpj`, `itens` com exatamente 1 `item` (só campos obrigatórios do item), `valorTotal`, `valorDeducao` com `xsi:nil`. Sem `razaoSocial`, `quantidade`, `dataCancelamento`, `observacao`. |
| 2 | Obrigatórios + opcionais | Mesmo caso acima acrescido de `razaoSocial`, `quantidade`, `dataCancelamento` e `observacao` preenchidos. |
| 3 | Cada ramo de `choice` | (a) instância com `tomadorPorDocumento`; (b) instância com `tomadorPorCadastroMunicipal`; (c) caso inválido: instância com os dois ramos presentes simultaneamente; (d) caso inválido: instância sem nenhum dos dois ramos. |
| 4 | Repetição/coleção | (a) `itens` com 0 elementos `item` (válido, mas gerar aviso de negócio); (b) `itens` com 1 elemento; (c) `itens` com N elementos (ex.: 5), variando presença de `quantidade` em alguns itens. |
| 5 | Nulo vs. ausente | (a) `valorDeducao` com `xsi:nil="true"`; (b) `valorDeducao` com valor numérico; (c) caso inválido: `valorDeducao` ausente do XML; (d) `dataCancelamento` ausente (válido); (e) `dataCancelamento` presente com data válida (válido); (f) caso inválido: `dataCancelamento` com `xsi:nil="true"`. |
| 6 | Limites e patterns | `codigoMunicipio` com 7 dígitos (válido) e com 6 ou 8 dígitos (inválido); `cpfCnpj` com 11 e com 14 dígitos (válidos) e com 12 dígitos (inválido); `quantidade` no limite `minExclusive=0` (`0` é inválido, `0.0001` é válido). |
| 7 | Pacote/include | Reimportar o par de arquivos com `exemplo-composto-tipos.xsd` ausente do pacote (caso inválido esperado: falha determinística de resolução de `include`, não silenciosa). |

## 7. Ambiguidades / dúvidas registradas

Nenhuma ambiguidade neste par de arquivos que exija decisão em
`docs/DECISIONS.md` — os dois ramos do `choice` têm tipos estruturalmente
distintos (sem overlap de nomes de elemento nem de conteúdo), e a ordem dos
elementos na `sequence` principal é explícita.

**Atualizado na Fase 3**: o gap de `xs:import` entre namespaces diferentes
apontado aqui foi endereçado com um terceiro par de fixtures dedicado —
`docs/schemas/exemplo-import-ns-a.xsd` (módulo importado) e
`exemplo-import-ns-b.xsd` (módulo raiz, importa o primeiro via `xs:import`
com `namespace`+`schemaLocation`, reusando `EnderecoType` de outro
namespace). Ver `Tradux.Xsd.Tests.CrossNamespaceImportTests`. Este par
(`exemplo-composto.xsd` + `exemplo-composto-tipos.xsd`) continua cobrindo
apenas `xs:include` (mesmo namespace), que é o caso mais comum em layouts
fiscais reais (tipos compartilhados dentro do mesmo padrão municipal).
