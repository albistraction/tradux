# ADR-0021: Regras do DE-PARA em C# legível no conversor gerado

- **Status**: substituído
- **Substituído por**: ADR-0025 (o produto é só o microsserviço, 2026-11-16)
- **Data**: 2026-10-09
- **Fase**: 17
- **Revê**: ADR-0018, alternativa rejeitada "Gerar as regras de DE-PARA em C#"

## Contexto

O usuário pediu: "ao gerar o conversor tem que aplicar as regrinhas também".
Na pergunta de escolha, optou por **regras em C# legível** no arquivo gerado
(ex.: `Numero = F(t, "nDPS")`, tabela DE-PARA visível). Não quis só embutir o
template.

O ADR-0018 havia rejeitado gerar as regras em C#. O motivo: isso duplicaria o
motor, e o resultado divergiria da API (regra "SDK e API são fachadas
equivalentes").

## Decisão

1. **Regras legíveis, implementação única.**
   - Cada regra do mapeamento (valor + condição, coleção, filtro, ordenação)
     vira um método C# em `{P}Regras`, com o caminho de destino e a regra
     descritos no comentário.
   - Operadores chamam **as mesmas implementações** do motor
     (`Ops.Coalesce`, `Ops.Lookup`, … = `RuleOperatorMethods`).
   - Condições usam `Truthy.Evaluate`, a mesma avaliação do motor.
   - Tabelas DE-PARA viram dicionários em `{P}Tabelas`.
   - Nada é reimplementado: a tradução é só de forma.
2. **Estrutura continua no motor.**
   - `TemplateCompiler.Compile(…, codeRules)` usa as funções C#
     (`TemplateCodeRules`) no lugar das expressões do mapeamento.
   - Cardinalidade, choice, obrigatório/nil/padrão, formatação, eventos e
     validação XSD seguem pelo mesmo código de sempre.
   - O mapeamento embutido define a estrutura e é **conferido** contra as
     regras: regra faltando (`CODE_RULE_MISSING`) ou sobrando
     (`CODE_RULE_UNUSED`) recusa o carregamento.
3. **Conversor autossuficiente.**
   - O arquivo embute o conteúdo da versão (XSDs, mapeamento, tabelas)
     (`{P}Conteudo`) e roda por `EmbeddedTemplate` (SDK). `EmbeddedTemplate`
     usa o mesmo `XmlGenerationService` da API e do SDK: limites, XML de
     origem, diagnósticos e prazos.
   - Não precisa de MongoDB nem da API.
   - O hash do conteúdo embutido é conferido ao carregar: arquivo alterado é
     recusado.
   - Com um `TraduxRuntime` opcional, o conversor ainda confere se o
     template foi republicado (`TemplateVersionPolicy`).
4. **Operador nativo substituído.** Só a definição nativa do catálogo
   (`RuleOperatorDefinition.IsBuiltIn`) vira `Ops.*`. Um registro de terceiros
   com nome de nativo vira `OperadorCustomizado`. O `EmbeddedTemplate` recusa
   um catálogo que substitua um operador que o código chama como nativo
   (`TemplateCodeRules.NativeOperators`).
5. **Exceções.** As regras em C# passam pela mesma rede de segurança do motor:
   uma exceção .NET vira `RULE_EXECUTION_FAILED`, não derruba a geração.
6. **Texto do template no código.** Só entra como literal C# escapado. Em
   comentário, sai sem quebra de linha nem controle/separador Unicode.
7. **Operador customizado.** Vira `OperadorCustomizado("nome", [args])`, um
   método parcial que o integrador **precisa** implementar; sem isso, o
   arquivo não compila. O catálogo com o operador é passado ao conversor.
8. **Gate de equivalência.** Para cada template de teste, o conversor gerado e
   o motor dão o mesmo sucesso, o mesmo XML e os mesmos diagnósticos. Isso vale
   nos cenários do autor e em **todos** os casos da suíte estrutural, válidos e
   inválidos, e inclui um template que usa todos os operadores do catálogo,
   condição, filtro, ordenação, escopo raiz e objeto único.

## Alternativas rejeitadas

- **Reescrever os operadores em C# "puro"** (`??` no lugar de `Coalesce`,
  `switch` no lugar de `Lookup`): fica mais idiomático, mas as semânticas
  divergem em detalhes. Exemplos: `Coalesce` ignora só nulo; `Lookup` com
  chave numérica normaliza o texto. Seria o motor duplicado.
- **Só embutir o template (sem regras em C#):** o usuário pediu as regras
  visíveis no código.

## Consequências

- O conversor gerado tem 2 a 3 mil linhas num layout municipal real, porque
  inclui os XSDs embutidos. As regras ficam numa classe própria, fácil de
  ler.
- Editar a regra no `.g.cs` não é o caminho: a regra muda no template e o
  arquivo é regenerado. O cabeçalho e o comentário de `{P}Regras` dizem isso.
  Editar a tabela ou a regra à mão faz o código divergir do template, e isso
  só é detectado para regras faltando ou sobrando, não para regras alteradas.
- `GeneratorVersion` = 2. Conversores da versão 1 são regenerados pelo
  `tradux-codegen` no build.
