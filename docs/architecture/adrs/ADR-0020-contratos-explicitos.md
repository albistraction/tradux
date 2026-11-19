# ADR-0020: Contratos explícitos no lugar de convenções (saneamento da Fase 15)

- **Status**: aceito
- **Substituído em parte por**: ADR-0025 §8 (eventos de geração removidos, 2026-11-17)
- **Data**: 2026-10-09
- **Fase**: 16 — Saneamento
- **Substitui em parte**: ADR-0018 (itens 2, 5 e limitações) e ADR-0019 (algoritmo customizado)

## Contexto

Depois da Fase 15, o usuário perguntou se o projeto estava "virando uma
grande gambiarra" e pediu um framework sério. A auto-revisão encontrou
atalhos que funcionavam, mas dependiam de convenção implícita ou de estado
global:

1. **Coleção sobre objeto.** Virava lista de 1 item em silêncio. Um caminho
   errado deixava de ser detectado.
2. **`$root.`.** Era um prefixo mágico dentro da string do caminho, em vez de
   um tipo no modelo de regras.
3. **Choice na suíte estrutural.** Uma heurística (`SoleBranchContent`, que
   contava campos e cortava em 2) decidia os casos inválidos. Um choice único
   com dois ramos mapeados bloqueava a publicação.
4. **Eventos.** Eram ambientes (`AsyncLocal`, `events.Use()`). Uma publicação
   ou suíte de testes dentro do escopo dispararia os eventos do integrador.
5. **Assinatura customizada.** Dependia de registro global no `CryptoConfig`
   e de `AsyncLocal` para passar o algoritmo ao `SignatureDescription`.
6. **Conversor gerado.** As referências `$root` ficavam fora da entrada
   tipada.

## Decisão

1. **`CollectionMapping.SingleObjectAsItem`** (padrão: falso).
   - Ligado: um objeto no caminho conta como 1 item.
   - Desligado: objeto no caminho é erro `COLLECTION_SOURCE_NOT_ENUMERABLE`
     (o código anterior à Fase 15), com sugestão de ligar a opção.
   - Na suíte estrutural, quando 1 item, a coleção sai como objeto; vários
     saem como lista. As duas formas são exercitadas.
   - No editor, o bloco "for each" oferece objetos como "objeto único: 1
     item" e grava a opção.
2. **`FieldReferenceNode.Scope`** (`current` | `root`).
   - O padrão (`current`) não é serializado, então o conteúdo e o hash de
     templates publicados não mudam.
   - `root` lê `SourceDataTree.Root`, a raiz da entrada, mesmo dentro de um
     "for each".
   - O prefixo `$root` e as chaves mágicas na árvore de dados deixam de
     existir.
   - No editor, campos da raiz aparecem como "(raiz) …" dentro dos blocos.
   - No conversor gerado, as referências de raiz entram na entrada tipada da
     raiz.
3. **Suíte estrutural com ramos de choice reais.**
   - O perfil registra, para cada uso de campo ou coleção, os ramos de choice
     que o envolvem e se o uso é condicional (`BranchUse`).
   - Cada caso escolhe ramos: um ramo padrão por choice único (de preferência
     um com obrigatório e sem condição) e todos os ramos num choice
     repetível.
   - A seleção é fechada: um campo presente preenche todos os ramos em que é
     usado sem condição. Esses ramos entram no caso com os outros campos
     deles, porque aparecem de qualquer jeito, como o motor faria.
   - Casos novos `ramo:` (categoria `Choice`) cobrem cada ramo mapeado sozinho.
     Não há produto cartesiano entre choices.
   - Um ramo só com conteúdo condicional vira lacuna declarada, coberta pelos
     cenários funcionais.
   - "Conteúdo sem dado" vem do próprio motor (`RuleExecutor.ContentWithoutData`):
     cada regra é avaliada com a entrada vazia. Esse conteúdo força o ramo só
     se a regra **não tem condição** e se está **fora** de item de coleção
     (sem item, não há conteúdo). Com condição (ex.: cNaoNIF quando falta o
     CPF), o ramo é alternativa dependente do dado. Aí a ausente de um campo
     de outro ramo do mesmo choice não é tratada como inválida esperada.
     O autor precisa cobrir o ramo condicional com um **cenário funcional**:
     a suíte declara a lacuna e não a exercita.
   - Ausente/nulo é "inválido esperado" só quando o motor de fato recusa: o
     ramo fica parcial, ou o choice fica vazio. Se outro ramo do mesmo choice
     segue no caso, não é. A regra é calculada, não estimada.
4. **Eventos explícitos.**
   - Os eventos são passados para uma geração:
     - `XmlGenerationEngine.Generate(template, data, events)`;
     - `TraduxRuntime.GenerateXmlAsync(request, events)` e
       `TransformXmlAsync(request, events)`;
     - o conversor gerado passa os seus `Events` a cada conversão.
   - Não existem mais `GenerationEvents.Current` nem `Use()`.
   - Publicação, testes e outras gerações concorrentes nunca veem os eventos
     de alguém.
5. **Assinatura sem estado global.**
   - Sai `ICustomSignatureAlgorithm`. "Customizável" passa a significar que a
     chave é do integrador: `XmlSignatureOptions.SigningKey` aceita qualquer
     `RSA`/`ECDsa`, seja provedor de HSM, token A3 ou nuvem.
   - `DelegatedRsa`/`DelegatedEcdsa` delegam só a operação de assinar o hash.
   - O algoritmo continua padrão do XMLDSig, então qualquer verificador
     (inclusive a prefeitura) aceita.
   - O único registro global restante é o da descrição padrão de
     ECDSA-SHA256. Ela é sem estado e registrada uma vez, que é o mecanismo de
     extensão previsto pelo .NET.
   - `XmlSigner.Inspect` diz o que cada assinatura cobre (URIs) e por que
     falhou.
   - Id duplicado é referência ambígua, ou seja, risco de *signature
     wrapping*. Agora é recusado também com atributo de Id customizado.
6. Sem mudança de semântica para templates publicados que não usavam as
   convenções removidas.

## Alternativas rejeitadas

- **Manter `$root.` como prefixo e documentá-lo:** continuaria sendo uma
  convenção de texto que nenhum tipo protege. Um campo chamado `$root` na
  entrada colidiria com ela.
- **Algoritmo de assinatura com URI própria (não padrão):** exige registro
  global e só o próprio verificador entenderia. As prefeituras aceitam
  RSA-SHA1/SHA256. O ponto de customização real é onde a chave está.
- **Produto cartesiano de ramos de choice na suíte:** viola a regra de
  cobertura determinística (`.claude/rules/testing.md`).

## Consequências

- Mudanças de contrato público:
  - `GenerationEvents.Use()`/`Current` foram removidos;
  - `ICustomSignatureAlgorithm`/`SignatureAlgorithm.Custom` foram removidos;
  - `GeneratedTemplateGuard.PinnedVersion` (nulo com `RequireSameVersion`)
    virou `Version` (sempre a versão do código). Conversores gerados antes da
    Fase 16 precisam ser regenerados; o `tradux-codegen` no build já faz isso.

  As duas surgiram na Fase 15, na mesma semana, e o único consumidor era o
  exemplo, que foi migrado.
- Quem dependia de "objeto vira 1 item" sem a opção passa a receber erro
  claro. Isso só existiu de forma implícita na Fase 15.
- Choice único em que um ramo nunca sai sozinho (outro ramo sempre aparece:
  campo usado também fora do choice, ou constante) reprova a publicação com
  `CHOICE_BRANCH_UNREACHABLE`: qualquer entrada com aquele ramo geraria dois
  ramos no motor. Antes, a falha só aparecia na geração.
- A seleção de ramos de um campo para casos inválidos usa o primeiro uso dele.
  Um campo usado em ramos diferentes é testado no primeiro; os demais ficam
  cobertos pelos casos `ramo:`.
- Continuam abertos:
  - N ocorrências do **mesmo** ramo dentro de um choice repetível (ADR-0018);
  - verificação de cadeia do certificado: `Verify` sem certificado continua
    provando só integridade.
