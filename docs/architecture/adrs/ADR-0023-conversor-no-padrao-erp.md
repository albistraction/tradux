# ADR-0023: Conversor gerado no padrão dos conversores do ERP

- **Status**: substituído; substituído em parte pelo ADR-0024 (helper em C# puro, sem contexto e sem o motor, C# 7.3)
- **Substituído por**: ADR-0025 (o produto é só o microsserviço, 2026-11-16)
- **Data**: 2026-10-13
- **Fase**: 20
- **Complementa**: ADR-0021 (regras em C# no conversor) e ADR-0022 (runtime netstandard2.0)

## Contexto

O conversor da Fase 17 (ADR-0021) saía num arquivo único: `{P}Template`,
`{P}Tags`, `{P}Tabelas`, `{P}Regras`, `{P}Entrada`, `{P}Saida`,
`{P}Converter` e o template embutido em JSON, executado pelo motor. O usuário
recusou esse formato ("tudo em uma classe?"). Ele pediu que o conversor fosse
igual aos conversores do ERP.

Os conversores do ERP seguem este padrão:
- classes de serialização do XSD do provedor, geradas pelo xsd.exe, uma por
  arquivo, em `DataAccess/Serializacao/NFSe/<Provedor>`;
- um `<Provedor>ConversorHelper` estático com métodos de extensão `ParaX` por
  tipo (ex.: `ParaAdm(this Valores valores, …, TCDPS dps = null)`). Esses
  métodos leem o modelo do ERP (`TCDPS`, `TInfErp`) e devolvem as classes
  do provedor;
- serialização com `XmlSerializer`.

O usuário escolheu ler a `TCDPS`/`TInfErp` que o ERP já tem. A
alternativa, o `Rps` antigo, foi descartada.

## Decisão

1. **Novo formato de geração, "ERP"**: `CSharpConverterGenerator.GenerateErp`.
   - Fachadas: `TraduxRuntime.GenerateErpConverterAsync` (SDK),
     `GET …/codegen/erp` (API, ZIP), `tradux-codegen --format erp` (CLI) e o
     editor, onde é o formato padrão. O arquivo único continua disponível.
   - Saída, em arquivos separados:
     - `Serializacao/{Provedor}/*.cs`: uma classe por tipo de destino, no
       estilo xsd.exe. Tipo nomeado vira classe com o nome do tipo; tipo
       anônimo vira pai + elemento. Lista com invólucro vira `[XmlArray]` +
       `[XmlArrayItem]`. Escalares são `string`, já no texto do XSD.
       Obrigatório + nillable usa `IsNullable`; o atributo fixo da raiz já
       vem preenchido.
     - `Conversores/{Provedor}ConversorHelper.cs`: um método de extensão
       `Para{Provedor}{Elemento}` por tipo complexo (raiz: `Para{Provedor}` e
       `Para{Provedor}Xml`).
       - O `this` é a classe de entrada do escopo: a raiz (`TCDPS`) ou o item
         do "para cada" (`TCInfDPS`).
       - As regras do DE-PARA são escritas em C#: `Ops.*`, tabelas como
         dicionários e caminhos como `dps.infDPS?.prest?.CNPJ`.
     - `Conversores/{Provedor}Contexto.cs`: os blocos próprios da raiz do
       contrato de entrada (ex.: `infExtra`) viram parâmetros do contexto
       (`contexto.ChaveSeguranca`). O contexto herda de `ConversorContexto`.
     - Opcional (`inputModel`): `Entrada/*.cs`, as classes de entrada no
       estilo xsd.exe, para sistemas que ainda não as têm.
2. **Classes de entrada pela convenção do xsd.exe.** Os nomes vêm dos tipos
   do XSD de origem: `SourceShapeNode.TypeName` e `SourceShape.RootTypeName`.
   - A propriedade tem o nome do elemento; o atributo, o nome sem `@`; o
     texto vira `Value`.
   - Lista com invólucro é o próprio array.
   - `InputRootType` renomeia a raiz quando o sistema usa outro nome (o XSD
     do exemplo diz `TCDPSErp`; o ERP tem `TCDPS`).
   - O valor lido é normalizado como o motor normaliza o XML de origem
     (`ValorOrigem.Numero/Data/Texto`). Por isso a propriedade pode ser
     `string`, `decimal`, `decimal?`, `DateTime` ou enum.
   - **Par `XxxSpecified` do xsd.exe**. Nas classes do sistema, campo
     opcional de tipo valor sem informação vale `0`, a data mínima ou o 1º
     item do enum.
     - A folha é lida pelo dono: `ValorOrigem.Numero(dps.infErp?.Valores,
       "ValorLiquidoNFSe", o => o.ValorLiquidoNFSe)`.
     - Com `ValorLiquidoNFSeSpecified == false`, o valor é nulo, como no
       motor. Assim o `coalesce` cai no próximo e o obrigatório reprova.
     - Foi a revisão da Fase 20 (B1) que apontou isso.
   - As classes de destino entram sempre pelo alias `using Destino = …;`
     (`Destino.RpsNfse`). Os namespaces de entrada do sistema podem ter
     classes com o mesmo nome (`TCDPS`, `TCEndereco`), e um `using` simples
     ficaria ambíguo.
   - Só o método raiz (`Para{Provedor}`) chama `Concluir` e transforma os
     erros acumulados em exceção. Os `Para…` filhos são públicos, como no ERP,
     mas quem os chama direto confere `contexto.Erros`.
3. **Sem motor em tempo de execução.** O código gerado usa só o pacote
   `Tradux.Engine` (netstandard2.0):
   - os operadores (`RuleOperatorMethods`) e `Truthy`;
   - `XsdScalarText`, a formatação de escalar. Ela foi extraída do
     `XmlDocumentBuilder`, então motor e código formatam com o mesmo código;
   - `ConversorContexto`, com as regras de estrutura do motor:
     - obrigatório sem valor → `REQUIRED_VALUE_MISSING`;
     - valor fora do tipo → `VALUE_CONVERSION_FAILED`;
     - `Ramo` do choice entra só com conteúdo e descarta os erros de dado
       ausente;
     - `Escolha`, para a cardinalidade do choice;
     - `Colecao`, para filtro, ordem, `$index` antes do filtro e cardinalidade;
     - erro de regra → `ConversaoNfseException` com os mesmos códigos;
   - `ConversorXml` (XmlSerializer).
4. **Gate de equivalência** (`ErpConverterEquivalenceTests`). Cada XML de
   origem é desserializado nas classes de entrada, como o ERP faz com a DPS,
   e convertido pelo código gerado (compilado em **C# 12**, sem avisos).
   - O resultado tem de ser o mesmo XML do motor, por comparação semântica,
     ou falhar quando o motor falha.
   - Fixtures: estilo webservice (choice repetível, invólucro, objeto único
     como item, escopo raiz, bloco próprio, padrão do schema, nillable,
     filtro/ordem/`$index`) e ERP (atributo, texto de conteúdo simples,
     `xsi:nil`, bloco próprio com lista).

## Consequências

- Prova em CI: `Tradux.NetFramework.Tests` gera, compila (C# 12) e roda
  o conversor ERP no .NET Framework 4.8 (XmlSerializer do framework).
- O conversor fica igual a um conversor do ERP. O mantenedor do ERP lê os
  métodos `Para…`, sem precisar do motor nem do template embutido.
- Prova real: no exemplo (`erp-conversor/`), o código gerado compila em
  .NET Framework 4.8 / C# 12 contra as classes **reais** do ERP. A
  `LayoutNacional100` e o xmldsig ficam só linkados; nada no ERP é alterado.
  O XML gerado é igual ao do motor para a DPS 14938.
- Limites conhecidos (geração recusada com mensagem clara):
  - ramo de `xs:choice` que é grupo mapeado;
  - "para cada" sobre lista de valores simples;
  - origem de coleção que é objeto sem "objeto único como item";
  - campo próprio dentro de um bloco do XSD de origem: a classe do sistema
    não o tem, então ele precisa ir para um bloco na raiz;
  - `xs:choice` da origem na forma pura do xsd.exe (`Item`/`ItemElementName`).
    O ERP achatou esses choices em propriedades; outra forma não compila e
    aparece como erro de compilação no projeto do integrador;
  - o mesmo elemento em duas posições da mesma sequência.
- Diferenças aceitas em relação ao motor:
  - o conversor ERP não valida o XML de **origem** contra o XSD de origem. O
    XmlSerializer aceita origem inválida; quem chama entrega a DPS que já tem;
  - `xs:dateTime` lido como `DateTime` local sai com o fuso da máquina:
    é o mesmo instante, mas o texto do fuso pode diferir. Um `DateTime` sem
    fuso à meia-noite sai só como data. Nas classes do ERP, `dhEmi` é
    `string` e os `DateTime` são `DataType="date"`;
  - elemento nillable obrigatório **dentro** de choice ou de grupo opcional,
    mapeado e sem valor: o motor escreve `xsi:nil` (e pode reprovar com
    `CHOICE_MULTIPLE_BRANCHES`); o conversor omite. Fora desses casos,
    `xsi:nil` só sai onde o motor o escreve;
  - o conversor ERP não valida contra o XSD de destino; o ERP já valida
    antes de enviar;
  - `omit` num elemento obrigatório e nillable sai como `xsi:nil`;
  - os eventos antes/depois de cada tag (ADR-0018) não existem nesse
    formato. Quem precisar deles usa o arquivo único.
- Classes do sistema com nomes fora da convenção do xsd.exe dão erro de
  compilação no projeto do integrador, nunca um XML errado em silêncio.
