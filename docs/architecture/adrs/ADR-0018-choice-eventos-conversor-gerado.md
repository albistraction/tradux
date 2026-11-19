# ADR-0018: xs:choice no destino, eventos do motor e conversor C# gerado

- **Status**: aceito; itens 2 (eventos ambientes) e 5 (objeto único implícito, `$root`) substituídos pelo ADR-0020
- **Substituído em parte por**: ADR-0025 (o conversor C# gerado e a CLI tradux-codegen, 2026-11-16; os eventos do motor, 2026-11-17)
- **Data**: 2026-10-09
- **Fase**: 15 — Choice, eventos, conversor gerado e versão do código

## Contexto

O usuário pediu, numa só sessão e sem perguntas:

1. eventos em C# antes e depois de preencher cada tag;
2. validação de versão, para que, ao atualizar o template, o código também
   seja atualizado;
3. um gerador de conversor C# a partir do template, com tipos fixos e classes;
4. assinatura genérica da DPS (ver ADR-0019);
5. um exemplo com `webserviceNFSe.xsd` (municipal) e a DPS + `infErp`.

O XSD do webservice tem `Rps` com conteúdo `xs:choice maxOccurs="9999"`. O
motor não gerava `xs:choice` no destino, decisão que estava pendente. Para o
exemplo funcionar, isso entrou no escopo.

## Decisão

1. **`xs:choice`/`xs:all` e grupos aninhados no destino.**
   - Grupos são transparentes no mapeamento: cada elemento continua mapeado
     pelo nome dentro do conteúdo do elemento pai. Na árvore do editor, o nó
     do grupo existe só para exibir; os filhos usam o caminho do elemento.
   - Choice único: exatamente um ramo com conteúdo; mais de um gera
     `CHOICE_MULTIPLE_BRANCHES` (escolha por condição no mapeamento).
   - Choice repetível: todos os ramos com conteúdo, em ordem do schema; cada
     ramo conta como uma ocorrência dentro de min/max (`CHOICE_NO_BRANCH`,
     `CHOICE_TOO_MANY`).
   - Ramo sem conteúdo é descartado junto com os diagnósticos de obrigatório
     ausente: dado ausente num ramo não escolhido não é erro.
   - Choice obrigatório sem nenhum ramo mapeado gera
     `MAPPING_MISSING_REQUIRED_CHOICE`.
   - Só conteúdo opaco não é gerado: `xs:any`, conteúdo simples com
     atributos ou vazio (`ParticleRules`).
2. **Eventos do motor** (`Tradux.Engine.Events.GenerationEvents`),
   assinados em C# e ativos dentro de `events.Use()`, que é `AsyncLocal` e
   atravessa `await`:
   - `FieldResolving`: **antes** de preencher a tag. Troca o valor ou omite.
   - `ElementCreated`: **depois** de criar a tag. O XML ainda é validado.
   - `DocumentBuilt`: documento montado, antes da validação.
   - `XmlGenerated`: XML final válido. É o ponto de pós-processamento (ex.:
     assinatura); o que mudar aqui não é revalidado.

   Os eventos recebem o caminho com índices e o caminho do template (sem
   índices). Exceção num manipulador vira `GENERATION_EVENT_FAILED`. Eventos
   são código do integrador: nunca vêm da API HTTP nem de configuração.
3. **Conversor C# gerado** (`CSharpConverterGenerator`, Application). É um
   arquivo único e determinístico por versão do template, que depende só do
   SDK. Contém:
   - `{P}Template`: organização, template, operação, versão e hash;
   - `{P}Tags`: caminhos das tags (constantes para os eventos);
   - `{P}Entrada`: entrada tipada (do contrato de entrada/XSD de origem, ou
     das referências do mapeamento);
   - `{P}Saida`: XML de destino tipado, com `Parse`;
   - `{P}Converter`: `ConvertAsync` e `ConvertXmlAsync`, com `Events` e
     métodos parciais.

   Nenhuma regra de geração é copiada para o código: o conversor chama o
   motor pelo SDK. Superfícies:
   - SDK: `TraduxRuntime.GenerateConverterCodeAsync`;
   - API: `GET …/codegen/csharp` (`template.read`);
   - CLI: `tools/Tradux.CodeGen` (`tradux-codegen`). Só regrava se o
     código mudou e tem `--check` para CI.
4. **Versão do código × template** (`GeneratedTemplateGuard`, SDK):
   - `RequireSameVersion` (padrão): a versão ativa precisa ser a do código, e
     o hash precisa bater. Se não, `TemplateCodeOutdatedException` pede para
     regenerar.
   - `PinGeneratedVersion`: gera sempre com a versão do código.
   - A geração usa **sempre** a versão do código; a política só decide se
     uma versão ativa diferente é erro. Assim, dentro da janela de cache,
     uma versão nova nunca é usada com classes geradas da antiga.
   - A checagem fica em cache por 30 s, compartilhado no processo (chave:
     template, versão, hash e política), para não consultar o repositório a
     cada geração, mesmo com um conversor criado por requisição.
   - "Código atualizado junto com o template": o build roda o `tradux-codegen`
     (target MSBuild no exemplo) e o CI usa `--check`.
5. **Extensões do motor exigidas pelo exemplo real:**
   - `xs:dateTime` no destino (`SchemaScalarType.DateTime`).
   - Coleção sobre um **objeto único** conta como lista de 1 item (um
     documento vira um `Rps`); antes era erro.
   - `$root.caminho` lê a raiz da entrada dentro de um bloco "for each".
   - A árvore de destino informa `ContentRepeatable` (conteúdo repetível,
     ex.: choice com `maxOccurs` alto). O gerador usa isso para tornar os
     ramos listas.
   - A suíte estrutural exercita os ramos de choice e a coleção sobre objeto
     único. Quando um campo é o único conteúdo de um ramo de um choice com
     vários ramos, ausente/nulo nele não é "inválido esperado": o ramo só sai
     do XML, o que é válido.
6. **Segurança do código gerado:** texto do template entra no código só
   como literal escapado (inclusive U+2028/U+2029); no comentário do
   cabeçalho, sem quebras de linha. Mensagem de exceção de manipulador de
   evento não vai para o diagnóstico (só o tipo), porque pode carregar dado
   da entrada; cancelamento propaga.

## Alternativas rejeitadas

- **Mapeamento com nós de grupo explícitos:** mudaria o formato do
  `MappingTemplate` e quebraria templates existentes.
- **Gerar as regras de DE-PARA em C#:** duplicaria o motor e divergiria da
  API (regra de fachadas equivalentes). *Revisto no ADR-0021:* regras em C#
  legível que chamam os operadores do motor, com equivalência como gate.
- **Eventos configuráveis pela API (scripts):** seria execução de código
  arbitrário no servidor. Eventos ficam no código do integrador.
- **Atualização automática do código em execução:** código não se reescreve.
  O que o projeto faz é regenerar no build e checar na execução.

## Consequências

- Layouts municipais com choice (como o `webserviceNFSe.xsd`) passam a ser
  gerados. Um choice único com mais de um ramo mapeado precisa de condição.
  Na suíte estrutural, os dados sintéticos preenchem todos os ramos, então o
  caso reprova e a escolha fica a cargo do cenário funcional.
- O conversor gerado é público e versionado com o template. Mudança no
  gerador regenera o arquivo no próximo build.
- Limitações conhecidas (revisão da Fase 15):
  - Num choice repetível, cada ramo gera no máximo **uma** ocorrência por
    elemento pai (ramo com `maxOccurs=1` não vira coleção). Um lote com N
    RPS usa a repetição do elemento de fora (ex.: `Rps` repetível); N ramos
    iguais dentro do mesmo choice ainda não são geráveis.
  - A suíte estrutural não gera um caso por ramo: os casos sintéticos
    preenchem todos os ramos mapeados. Num choice **único** com mais de um
    ramo mapeado, o caso reprova e a publicação é bloqueada; nesse caso, use
    um ramo por template ou condição + cenário funcional, até existir caso por
    ramo.
  - Coleção apontando para um objeto agora vira 1 item (antes era erro); um
    caminho errado que aponte para um objeto não é mais sinalizado.
  - `XmlSigner.Verify` sem certificado informado só prova integridade (ver
    ADR-0019).
