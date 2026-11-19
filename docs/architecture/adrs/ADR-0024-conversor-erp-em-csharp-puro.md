# ADR-0024: Conversor no padrão do ERP em C# puro, sem motor

- **Status**: substituído
- **Substituído por**: ADR-0025 (o produto é só o microsserviço, 2026-11-16)
- **Data**: 2026-10-13
- **Fase**: 21
- **Substitui em parte**: ADR-0023 (formato do `{Provedor}ConversorHelper`, contexto e dependência do pacote do motor)

## Contexto

O usuário comparou o conversor da Fase 20 com o `municipioConversorHelper` feito à
mão no ERP e recusou o gerado. Pontos levantados:
- o gerado dependia do pacote `Tradux.Engine` (`ConversorContexto`, `ValorOrigem`,
  `Ops`, `ConversorXml`);
- tinha uma classe de contexto extra e um método por tipo cheio de chamadas
  `contexto.Texto(…, caminho, obrigatorio: true)`;
- o feito à mão é C# puro: `?.`/`??`, inicializadores aninhados, métodos privados;
- a entrada é sempre a `TCDPS` que vem do sistema emissor.

Decisões do usuário:
- a entrada é a classe do contrato de origem do template. Na geração, ele informa o
  namespace dessas classes, que refletem o XSD de origem;
- o gerador gera as classes do XSD de saída;
- o conversor não valida (o ERP valida o XML no XSD antes de enviar), igual ao feito
  à mão.

## Decisão

1. **Saída do formato ERP (gerador versão 2)**:
   - `Serializacao/{Provedor}/*.cs`: classes do XSD de saída tipadas como o xsd.exe:
     - `xs:decimal` → `decimal`;
     - `xs:int`/`long`/… → `int`/`long`/…;
     - `xs:boolean` → `bool`;
     - `xs:date` → `DateTime` (`DataType = "date"`);
     - `xs:integer` e derivados → `string` (`DataType`);
     - tipo valor que pode faltar (opcional, ramo de choice, grupo opcional) → par `XxxSpecified`;
     - exceções:
       - `xs:dateTime` → `string`: um `DateTime` trocaria o fuso recebido (`-03:00`) pelo da
         máquina;
       - enumeração → `string`, como nas classes do ERP;
       - `xs:int`/`long`/`short`/`byte`/sem sinal, `float`/`double` e `boolean` → `string`
         (revisão, I1). O motor escreve o texto do valor como veio (`007`, `1.50`, `1`). Tipar
         mudaria o texto e truncaria `10.5` em `int` em silêncio; o XSD do sistema reprova
         como reprovaria o do motor.
   - `Conversores/{Provedor}ConversorHelper.cs`:
     - `internal static partial class`, `[ExcludeFromCodeCoverage]`;
     - aliases `using {Provedor} = <saída>;` e `using {Último segmento} = <entrada>;`;
     - `public static {Provedor}.Raiz Para{Provedor}(this Entrada.TCDPS dps, <parâmetros>)`
       e `Para{Provedor}Xml` (XmlSerializer, UTF-8);
     - inicializadores aninhados;
     - ramo de choice ou elemento opcional que pode vir vazio: método privado
       `Montar…` que devolve `null` sem valor (como o `MontarInformacoesIBSCBS` feito à mão);
     - regras do DE-PARA em C# com métodos privados no próprio arquivo, gerados só
       quando usados (`DePara`, `FormatarData`, `Primeiro`, `Decimal`…), com a mesma
       semântica dos operadores e da formatação do motor;
     - tabelas DE-PARA como dicionários;
     - operador customizado: delegate `OperadorCustomizado`.
   - Sem `Contexto`: os blocos próprios da raiz do contrato viram **parâmetros do método**
     (`string chaveSeguranca, string inscricaoEstadualPrestador`).
   - **Nenhuma referência ao motor**: só o BCL. Compila em **C# 7.3**, o padrão de um
     projeto .NET Framework 4.8 sem `LangVersion`.
2. **Entrada pela convenção do xsd.exe**, a partir do XSD de origem do template:
   - `SourceShapeNode` passou a guardar o tipo primitivo do XSD (`XsdType`), se é
     enumeração e se a própria declaração é opcional (`DeclaredOptional`);
   - campo opcional de tipo valor (número, booleano, data, enumeração) é lido pelo par
     `XxxSpecified` (`dono?.XSpecified == true ? Numero(dono.X) : null`);
   - vale também quando o sistema trocou o enum por `string` e manteve o `Specified`,
     como o ERP fez;
   - a leitura passa por `Numero`/`Data`/`Texto`, que aceitam `string`, `decimal`,
     `enum` ou `DateTime`.
   - Se a classe do sistema diverge do XSD (ex.: XSD `xs:string`, classe `decimal`), o
     projeto do integrador **não compila**, nunca um XML errado em silêncio.
   - Opções: `InputNamespace` (namespace das classes do sistema) e `InputRootType`
     (ex.: `TCDPS`).
3. **Sem validação no conversor**, como o feito à mão:
   - obrigatório sem valor sai vazio ou `0`;
   - cardinalidade não é conferida;
   - erro de regra (chave fora da tabela, texto curto demais para `substring`, valor
     que não é número) lança `InvalidOperationException`, como o motor falha.
4. **Motor**: um elemento complexo opcional (`minOccurs="0"`) fora de choice só sai com
   conteúdo, como um ramo de choice. Antes, ele saía sempre e reprovava pelos filhos
   obrigatórios (caso `ImovelIBSCBS` do município de exemplo, exigido só por alguns indicadores de
   operação).
   - Sem conteúdo, os diagnósticos de dado ausente dele são descartados. Erro de regra
     continua reprovando.
   - Conteúdo é o mesmo do ramo de choice (`HasContent`): algum valor, valor padrão do
     schema ou `xsi:nil`.
   - Vale também para invólucro de lista opcional sem itens.
   - O conversor gerado segue a mesma regra: `Montar…` que devolve null e `NuloSeVazio`.
     O bloco sai direto quando sempre tem conteúdo (literal, valor padrão, obrigatório
     nillable).
   - **Consequência para templates já publicados:** um opcional mapeado sem dado, que antes
     saía `<X/>` (válido quando os filhos são todos opcionais), agora não sai. O hash do
     template não muda, então o `--check` do CLI e o arquivo único não acusam. É a forma
     certa (elemento opcional vazio não carrega informação), mas muda a saída da mesma
     versão.
5. **Gate de equivalência** (`ErpConverterEquivalenceTests`, em net10.0; o net48 compila
   em C# 7.3 e roda um conversor ERP no .NET Framework em `XmlPathOnNetFrameworkTests`).
   Fixtures:
   - estilo webservice, ERP e conflito de nomes;
   - `tipos`: origem `bool`/`int`/`enum`/`date`/`decimal` opcionais, saída `int`/`boolean`/
     `date`/`double`/`decimal`, invólucro opcional, `$index` em elemento opcional, opcional
     com valor padrão, com `xsi:nil` e vazio.

   Regras:
   - o código gerado, sem o motor e compilado em C# 7.3, produz o mesmo XML que o motor
     em toda entrada que o motor aceita;
   - onde o motor reprova por validação (XSD, obrigatório, cardinalidade), o código pode
     gerar;
   - erro de regra falha nos dois.

6. **Simplificação** (o usuário achou o conversor do município de exemplo complexo demais).
   - **Catálogo:** a regra tinha que ser montada com operadores de baixo nível (tabela de 288
     combinações para "se/senão", padRight + substring + trim para cortar texto, 22 equals para
     uma lista). Entraram três operadores no catálogo, nos dois formatos e no editor:
     - `ifElse(condição, então, senão)`: só o ramo escolhido é avaliado. No C# gerado vira
       ternário;
     - `truncate(texto, tamanho)`: corta o excesso; nulo continua nulo. No C# gerado vira
       `Limitar(x, 80)`;
     - `lookup(chave, tabela, padrão)`: o 3º argumento opcional devolve o padrão quando a chave
       é nula ou não está na tabela, em vez de erro. No C# gerado vira `DeParaOuPadrao`.
   - **Gerador:**
     - valor fixo sai pronto no tipo do XSD (`"1"`, `0.00m`), com a formatação do motor;
     - regra que nunca é nula (literal, `x ?? ""`, comparação, `concat`, `lookup` com padrão)
       dispensa a checagem de bloco vazio. Só fica `Montar…` para bloco que pode não existir,
       como no feito à mão;
     - a mesma condição em vários campos de um bloco é calculada uma vez (`var aplicar = …`);
     - `formatDate` em `xs:dateTime` dispensa o `DataHora`.

## Consequências

- O mantenedor do ERP lê o conversor como lê o feito à mão. O projeto do ERP não
  precisa do pacote do motor.
- O XSD de origem do template precisa refletir as classes de entrada do sistema. No
  exemplo municipal, o `infErp` do contrato trazia todos os valores como
  `xs:string`, mas a `TInfErp` do ERP tem `decimal` com `XxxSpecified`. O contrato
  foi corrigido; sem isso, `ValorLiquidoNFSe` não informado viraria 0.
- Limites novos (geração recusada com mensagem clara):
  - bloco próprio da raiz com objeto ou lista (vira parâmetro, precisa ser valor
    simples);
  - binário (`base64Binary`/`hexBinary`) mapeado.
- Diferenças aceitas em relação ao motor:
  - o conversor não valida (acima);
  - `omit` é tratado como sem valor;
  - elemento obrigatório e nillable sem valor dentro de ramo: o motor escreve
    `xsi:nil`, o conversor não;
  - `xs:boolean` da origem lido de uma propriedade `bool` sai `true`/`false`. A forma
    `1`/`0` do XML se perde no XmlSerializer;
  - `xs:dateTime` da origem numa propriedade `DateTime` sai no fuso da máquina e sem
    fração. Para o mesmo texto do motor, a classe de entrada deve trazer `string`, como a
    `TCDPS` do ERP (`dhEmi`).
- Classes referenciadas pelo nome (a raiz, os itens de "para cada" e os parâmetros dos
  `Montar…`) precisam estar no namespace de entrada. Classes de subnamespaces, como
  `LayoutNacional100.InfErp`, funcionam quando só são alcançadas por propriedades.
- O formato de arquivo único (ADR-0021) não muda.
