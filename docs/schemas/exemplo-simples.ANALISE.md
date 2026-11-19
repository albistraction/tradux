# Análise — exemplo-simples.xsd

> **Fixture ilustrativa e sintética**, criada pelo agente `domain-xsd` na
> Fase 1 para servir de alvo à Fase 2 (vertical slice do motor). **Não é** um
> layout oficial de nenhuma prefeitura ou fornecedor. Quando um XSD real for
> fornecido, ele deve ser analisado à parte (não substituir este arquivo).

Segue o roteiro da skill `xsd-contract-analysis`
(`.claude/skills/xsd-contract-analysis/SKILL.md`).

## 1. Arquivos e namespaces

- Arquivo único: `exemplo-simples.xsd`. Sem `include`/`import`.
- Namespace alvo: `http://exemplo.ilustrativo.tradux/dps/v1`, prefixo
  convencional `tns`. `elementFormDefault="qualified"`,
  `attributeFormDefault="unqualified"`.
- Nenhuma referência a schema externo fora do próprio arquivo — sem risco de
  import remoto/XXE neste fixture específico (ver seção 5 abaixo para a nota
  de segurança genérica, aplicável ao importador real).

## 2. Resumo estrutural

Elemento raiz `DPS` (tipo `DpsType`), um único nível de `sequence` com 10
elementos filhos escalares e 2 atributos. Sem `choice`, sem coleções
repetíveis, sem tipos recursivos — deliberadamente minimalista para a Fase 2.

## 3. Tabela de elementos e atributos

| Caminho | Tipo | minOccurs | maxOccurs | nillable | default / fixed | Observação |
|---|---|---|---|---|---|---|
| `DPS` (raiz) | `DpsType` | 1 | 1 | não | — | elemento raiz único |
| `DPS/@versao` | `xs:string` restrito (`pattern \d\.\d{2}`) | obrigatório (`use="required"`) | — | n/a | `fixed="1.00"` | atributo obrigatório com valor fixo — deve sempre ser emitido com esse valor exato |
| `DPS/@id` | `xs:ID` | opcional (`use="optional"`) | — | n/a | — | identificador único no documento, se presente |
| `numero` | `xs:string` | 1 | 1 | não | — | obrigatório |
| `serie` | `xs:string` (`maxLength=5`) | 0 | 1 | não (default) | — | opcional; se presente, não pode ser `xsi:nil` |
| `dataEmissao` | `xs:date` | 1 | 1 | não | — | obrigatório |
| `competencia` | `xs:gYearMonth` | 0 | 1 | não | — | opcional |
| `valorServico` | `ValorMonetarioType` (`xs:decimal`, min 0, max 999999999.99, 2 casas) | 1 | 1 | não | — | obrigatório |
| `valorDesconto` | `ValorMonetarioType` | 1 | 1 | **sim** | — | obrigatório **e** nillable — ver caso de borda |
| `codigoServico` | `CodigoServicoType` (`pattern \d{2}\.\d{2}\.\d{2}`) | 1 | 1 | não | — | obrigatório |
| `cnpjPrestador` | `Cnpj14Type` (`pattern \d{14}`) | 1 | 1 | não | — | obrigatório |
| `cnpjTomador` | `Cnpj14Type` | 0 | 1 | não | — | opcional |
| `discriminacaoServico` | `xs:string` | 0 | 1 | não | — | opcional, texto livre |
| `codigoSituacao` | `SituacaoDpsType` (enum `1`,`2`,`3`) | 1 | 1 | não | `default="1"` | obrigatório, com default |

## 4. Casos de borda

1. **Obrigatório + nillable simultâneos (`valorDesconto`)** — o elemento deve
   sempre aparecer no XML (`minOccurs=1`), mas seu conteúdo pode ser
   explicitamente nulo via `xsi:nil="true"`, distinto de emitir `0.00`. Isso
   é o caso central que a regra "opcional ≠ aceita nulo" (seção 11 do
   requisito de origem) exige tratar sem colapsar em um único booleano no
   modelo canônico.
2. **Opcional sem nillable (`serie`, `competencia`, `cnpjTomador`,
   `discriminacaoServico`)** — podem faltar do XML inteiramente, mas se
   presentes, não podem carregar `xsi:nil` (schema não declara
   `nillable="true"`); um validador estrito deve rejeitar `xsi:nil` nesses
   elementos.
3. **Atributo obrigatório com valor fixo (`@versao`)** — combinação
   incomum: é obrigatório emitir o atributo, mas o motor não deve permitir
   que o usuário/mapeamento configure um valor diferente de `"1.00""`; o
   valor é dado pelo schema, não pelos dados de origem.
4. **Atributo opcional do tipo `xs:ID` (`@id`)** — exercita unicidade de
   identificador dentro do documento; se usado, deve ser único no XML
   gerado (regra de identidade XML, não de cardinalidade).
5. **Pattern de CNPJ sem dígito verificador** — `Cnpj14Type` só garante 14
   dígitos numéricos; validação de dígito verificador de CNPJ é regra de
   negócio fora do escopo do schema (não deve ser implementada como
   restrição XSD).
6. **Enumeração pequena e fechada (`codigoSituacao`)** — só aceita `1`, `2`
   ou `3`; qualquer outro valor (inclusive vazio) é inválido. Tem `default`
   aplicável apenas quando o elemento está ausente do XML de entrada antes
   da geração — não se aplica a valor vazio explícito.
7. **Limites numéricos de `valorServico`/`valorDesconto`** — `minInclusive=0`
   (não aceita negativo), `maxInclusive=999999999.99`, exatamente 2 casas
   decimais (`fractionDigits=2`); valores como `100.001` ou `-1.00` são
   inválidos.

## 5. Nota de segurança (importação de XSD)

Este fixture não referencia nenhum schema externo, então não há risco de XXE
ou path traversal *neste arquivo específico*. Ainda assim, ao implementar o
importador real (fora do escopo deste agente — especialista .NET), sinalizar:

- Resolver `xsi:schemaLocation`/`xs:import`/`xs:include` sempre dentro dos
  limites do pacote configurado, nunca seguindo URLs absolutas ou caminhos
  `../` para fora do diretório do pacote.
- Desabilitar resolução de entidades externas (DTD) no parser XML usado para
  ler XSD e para validar instâncias — risco clássico de XXE.
- Aplicar limites de tamanho de arquivo e profundidade de schema antes de
  compilar, mesmo para pacotes de um único arquivo.

## 6. Fixtures mínimas de dados necessárias (Fase 2 / Fase 8)

| # | Cenário | Descrição |
|---|---|---|
| 1 | Apenas obrigatórios | `numero`, `dataEmissao`, `valorServico`, `valorDesconto` (com `xsi:nil`), `codigoServico`, `cnpjPrestador`, `codigoSituacao`; sem `serie`, `competencia`, `cnpjTomador`, `discriminacaoServico`. |
| 2 | Obrigatórios + opcionais | Mesmo caso acima acrescido de `serie`, `competencia`, `cnpjTomador` e `discriminacaoServico` preenchidos. |
| 3 | Limites | `valorServico`/`valorDesconto` em `0.00` e em `999999999.99`; `serie` com exatamente 5 caracteres (limite de `maxLength`); um caso com 6 caracteres para validar rejeição. |
| 4 | Enumeração completa | Três instâncias válidas, uma para cada valor de `codigoSituacao` (`1`, `2`, `3`), e uma instância inválida com `codigoSituacao="9"`. |
| 5 | Nulo vs. ausente | (a) `valorDesconto` com `xsi:nil="true"`; (b) `valorDesconto` com valor numérico normal; (c) caso inválido: `valorDesconto` totalmente ausente do XML (viola `minOccurs=1`); (d) caso inválido: `serie` com `xsi:nil="true"` (viola ausência de `nillable`). |
| 6 | Pattern inválido | `cnpjPrestador` com 13 dígitos ou com letras; `codigoServico` sem os pontos (`"010101"`); `@versao` com valor diferente de `"1.00"`. |
| 7 | Caso inválido de atributo | Documento sem `@versao` (viola `use="required"`); documento com dois elementos usando o mesmo `@id` (viola unicidade de `xs:ID`, se exercitado em conjunto com outro elemento `xs:ID` no futuro). |

## 7. Ambiguidades / dúvidas

Nenhuma ambiguidade material neste fixture — ele foi desenhado para ser
inequívoco (schema próprio, sem dependências externas). Nenhuma entrada
adicional necessária em `docs/DECISIONS.md` por causa deste arquivo.
