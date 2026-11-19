# Roadmap por fases

Fonte normativa completa: `docs/requirements/PROMPT_MESTRE_CLAUDE.md`,
seção 27. Este documento é a versão operacional — mantenha os dois em sincronia
se o escopo de uma fase mudar (registre a mudança em `docs/DECISIONS.md`).

Regra geral: uma fase só é declarada concluída quando o **Gate** correspondente
é verdadeiro e verificável, não quando as entregas "parecem" prontas.

## Status das fases

| Fase | Nome | Status |
|---|---|---|
| 0 | Bootstrap de IA e governança | Concluída (gate cumprido em 2026-09-28) |
| 1 | Descoberta técnica e arquitetura executável | Concluída (gate cumprido em 2026-09-28) |
| 2 | Vertical slice do motor | Concluída (gate cumprido em 2026-09-28) |
| 3 | XSD completo e contratos compostos | Concluída (gate cumprido em 2026-09-28) |
| 4 | Motor de regras e coleções | Concluída (gate cumprido em 2026-09-28) |
| 5 | API, MongoDB, cache e SDK público | Concluída (gate cumprido em 2026-09-29) |
| 6 | Editor visual | Concluída (gate cumprido em 2026-09-29) |
| 7 | Camadas, versões e publicação | Concluída (gate cumprido em 2026-09-30) |
| 8 | Qualidade automatizada | Concluída (gate cumprido em 2026-09-30) |
| 9 | Segurança, autorização e observabilidade | Concluída (gate cumprido em 2026-09-30) |
| 10 | Pacotes, RAG e documentação operacional | Concluída (gate cumprido em 2026-09-30) |
| 11 | Performance, resiliência e prontidão | Concluída (gate cumprido em 2026-09-30) |
| 12 | Contrato de origem XSD e entrada XML | Concluída (gate cumprido em 2026-09-30) |
| 13 | Contrato de entrada versionado com extensões | Concluída (gate cumprido em 2026-10-01) |
| 14 | Mudanças do XSD de destino e remapeamento | Concluída (gate cumprido em 2026-10-02) |
| 15 | Choice, eventos, conversor gerado e assinatura | Concluída (gate cumprido em 2026-10-09) |
| 16 | Saneamento: contratos explícitos | Concluída (gate cumprido em 2026-10-09) |
| 17 | Regras em C# legível no conversor gerado | Concluída (gate cumprido em 2026-10-09) |
| 18 | Runtime portátil (netstandard2.0) para o ERP | Concluída (gate cumprido em 2026-10-09) |
| 19 | Template do ERP pela ferramenta: rascunho importável e conversor C# pelo editor | Concluída (gate cumprido em 2026-10-09) |
| 20 | Conversor gerado no padrão dos conversores do ERP (classes de serialização + ConversorHelper) | Concluída (gate cumprido em 2026-10-13) |
| 21 | Conversor no padrão do ERP em C# puro (sem motor, sem contexto, C# 7.3) e template do município de exemplo pelo manual | Concluída (gate cumprido em 2026-10-13) |
| 22 | Só o microsserviço: templates cadastrados e versionados pelo serviço, XML de origem → XML convertido | Implementada em 2026-11-16; aguardando revisão independente |

## Fase 18 — Runtime portátil (netstandard2.0) para o ERP

**Objetivo**: o framework precisa servir o ERP (.NET Framework 4.8).
Escolha do usuário: as duas integrações, runtime dentro do ERP e API HTTP.
Ver ADR-0022.

**Gate**:
1. Domain, Xsd, Engine, Contracts, Application e Sdk compilam para
   `netstandard2.0` e `net10.0`, sem avisos; os pacotes trazem os dois alvos.
2. Teste rodando em .NET Framework 4.8 (geração, validação, conversor gerado
   compilado em C# 12 e executado) com o mesmo resultado do motor.
3. O teste de arquitetura protege o alvo `netstandard2.0`.
4. A suíte `net10.0` inteira continua verde; revisão independente.

## Fase 22 — O produto é só o microsserviço tradutor

**Objetivo**: pedido do usuário (2026-11-16): sem DLL separada; um microsserviço onde
os templates (DE-PARA XSD de origem → XSD de destino) são cadastrados e versionados pelo
próprio serviço, e que recebe o XML de origem de um template e devolve o XML convertido.
O editor parte dos dois XSDs. Detalhes no ADR-0025.

**Gate**:
1. Saem SDK, conversor gerado (API, CLI, editor), alvo netstandard2.0/net48, conector de
   assinatura, catálogo de contratos de origem e importação/exportação de rascunho; build sem
   avisos, testes de arquitetura sem as regras do SDK.
2. `PUT/GET /v1/templates…`, `POST /v1/templates/{nome}/convert` (XML puro; 422/404),
   versões, conteúdo da versão e rollback, com a organização da credencial, permissões e
   isolamento — `Phase22GateTests` com MongoDB real.
3. Publicar/reativar pela API atualiza o cache da própria instância (sem janela em que a
   versão antiga converte na mesma instância).
4. Editor: tela inicial com os templates do serviço; XSD de origem → XSD de destino →
   DE-PARA → teste → publicar a próxima versão; histórico, reativar e abrir uma versão sem
   perder regras (ida e volta em `node --test` e no e2e).
5. Implantação: Dockerfiles e docker-compose (MongoDB replica set, API, editor).
6. Build, testes, e2e no navegador e revisão independente.

## Fase 21 — Conversor do ERP em C# puro

**Objetivo**: pedido do usuário, comparando o gerado com o municipioConversorHelper
feito à mão: o conversor não depende do motor nem tem classe extra; a entrada é a
classe do contrato de origem (namespace informado na geração); as classes do XSD de
saída são geradas tipadas como o xsd.exe; o conversor não valida. Detalhes no ADR-0024.

**Gate**:
1. Gerador ERP versão 2 nas quatro fachadas (SDK, API `?inputNamespace=`, CLI
   `--input-namespace`, editor).
2. Equivalência: o código gerado, sem referência ao motor e compilado em **C# 7.3**,
   produz o mesmo XML que o motor em toda entrada que o motor aceita, exceto as diferenças
   aceitas no ADR-0024 (ex.: booleano `1` lido de propriedade `bool`); erro de regra
   falha nos dois (`ErpConverterEquivalenceTests`, net10.0). Em net48, um conversor ERP
   compilado em C# 7.3 roda no .NET Framework com o mesmo XML do motor
   (`XmlPathOnNetFrameworkTests`).
3. Motor: elemento complexo opcional sem conteúdo não sai (com teste).
4. Prova real: o conversor do template do município de exemplo (exemplo, versão 2, regras do
   manual 2026) compila em .NET Framework 4.8 / C# 7.3 contra a `TCDPS` real do ERP,
   sem o motor, e o XML sai igual ao do motor.
5. Build, testes, e2e (RASCUNHO OK) e revisão independente.

## Fase 20 — Conversor no padrão dos conversores do ERP

**Objetivo**: pedido do usuário, que recusou o arquivo único ("tudo em uma
classe?"). O conversor gerado deve ficar igual aos conversores do ERP:
- classes de serialização do XSD do provedor, uma por arquivo, no estilo
  xsd.exe;
- `{Provedor}ConversorHelper` estático, com um método de extensão `Para…` por
  tipo, lendo a `TCDPS`/`TInfErp` que o ERP já tem (escolha do usuário);
- contexto com os parâmetros que a DPS não tem;
- `XmlSerializer`.

Detalhes no ADR-0023.

**Gate**:
1. Gerador do formato ERP (`GenerateErp`) nas quatro fachadas: SDK, API
   (`GET …/codegen/erp`, ZIP), CLI (`--format erp`) e editor, onde é o formato
   padrão. O arquivo único continua disponível.
2. Equivalência: o código gerado, compilado em C# 12 sem avisos, gera o mesmo
   XML que o motor, ou falha quando ele falha. A entrada é desserializada nas
   classes de entrada (`ErpConverterEquivalenceTests`).
3. Prova real: no exemplo, o conversor do template do ERP compila em
   .NET Framework 4.8 contra as classes **reais** do ERP (linkadas, sem
   alterar o ERP), roda, e o XML sai igual ao do motor.
4. API: `template.read`, isolamento por organização, 400 sem XSD de origem.
5. Build, testes, e2e (GATE OK e RASCUNHO OK) e revisão independente.

## Fase 19 — Template do ERP pela ferramenta

**Objetivo**: decisão do usuário, que não quis alterar o código do ERP. Montar o
template do `webserviceNFSe.xsd` como ele ficaria no ERP e carregá-lo na
ferramenta, gerando o conversor C# por ela.

A entrada é a DPS 1.01 + infErp (o que o ERP guarda no `Rps`) mais um bloco
próprio `infExtra` (chave de segurança do município, IE da filial).

**Gate**:
1. Rascunho importável gerado e conferido com o código do editor: o mapeamento
   reconstruído é idêntico ao DE-PARA.
2. Pela interface: importar, testar (válido e igual ao esperado), publicar
   (gates) e **gerar o conversor C#** (botão novo), automatizado no e2e
   (`--rascunho`).
3. O conversor gerado compila e roda num projeto .NET Framework 4.8 / C# 12,
   como ficaria no ERP.
4. O editor trata elemento com conteúdo `xs:choice` direto (ramos sem nó de
   grupo, ex.: `Rps`) como choice: ramo opcional, exige ao menos um.
5. Build, testes, e2e e revisão independente.

**Fora desta fase** (decisão do usuário): alterar o código do ERP. Isso inclui
o provedor genérico e a migração dos `*ConversorHelper`.

## Fase 17 — Regras em C# legível no conversor gerado

**Objetivo**: pedido do usuário ("ao gerar o conversor tem que aplicar as
regrinhas também"; escolheu regras em C# legível). Ver ADR-0021.

**Gate**:
1. Cada regra do template (valor + condição, coleção, filtro, ordenação) vira
   C# legível em `{P}Regras`, chamando os operadores do motor; as tabelas
   DE-PARA ficam em `{P}Tabelas`.
2. O conversor gera sem banco nem API (conteúdo embutido, hash conferido).
3. Equivalência: código gerado = motor (sucesso, XML, diagnósticos) nos
   cenários e em todos os casos estruturais, inclusive num template com todos
   os operadores.
4. Regra faltando ou sobrando e conteúdo alterado são recusados; operador
   customizado exige implementação.
5. O exemplo real converte com a API desligada; build, testes, e2e e revisão.

## Fase 16 — Saneamento: contratos explícitos

**Objetivo**: pedido do usuário ("tem que ser um framework sério"). Trocar os
atalhos da Fase 15 por contratos explícitos, sem funcionalidade nova (ADR-0020).

**Gate**:
1. Objeto no lugar da lista só com `SingleObjectAsItem`; sem a opção, erro
   claro (`COLLECTION_SOURCE_NOT_ENUMERABLE` com sugestão).
2. `FieldScope.Root` lê a raiz em "for each" aninhado e em filtro; `$root.` não
   compila; o padrão não muda o JSON nem o hash de templates publicados.
3. Eventos valem só para a geração que os recebe; gerações concorrentes não se
   misturam; nada de `AsyncLocal` nem `Use()`.
4. Suíte estrutural: um caso por ramo de choice; choice único com dois ramos
   mapeados publica; ramo forçado por campo compartilhado ou constante entra no
   caso; ramo inalcançável reprova com `CHOICE_BRANCH_UNREACHABLE`; ausente é
   inválido esperado só quando o motor de fato recusa.
5. Assinatura sem estado global: chave delegada (RSA e ECDSA), chave que não é
   par do certificado recusada, `Inspect` mostra o elemento assinado, Id
   duplicado recusado.
6. Editor: campos "(raiz)" e "for each" sobre objeto único.
7. Exemplo DPS + `infErp` → `webserviceNFSe` republicado pelos gates com os
   contratos novos e rodando de ponta a ponta.
8. Build sem avisos, todos os testes, e2e no navegador e revisão independente.

## Fase 15 — Choice, eventos, conversor gerado e assinatura

**Objetivo**: pedido do usuário (ADR-0018, ADR-0019).
- Eventos em C# antes e depois de cada tag.
- Validação de versão entre template e código.
- Conversor C# gerado do template, com tipos e classes.
- Assinatura genérica da DPS por tag e algoritmo, fora do núcleo.
- Um exemplo real: DPS + `infErp` → `webserviceNFSe.xsd`. Esse XSD exige
  `xs:choice` no destino.

**Gate**:
1. Template com `xs:choice` repetível no destino publica (gates
   estrutural e funcional) e gera XML válido.
2. Os eventos antes e depois alteram a geração.
3. O conversor gerado compila e converte (entrada XML e tipada) e lê a
   saída tipada.
4. Com o template atualizado, o código antigo recusa (`RequireSameVersion`)
   ou usa a versão fixada (`PinGeneratedVersion`).
5. O `tradux-codegen` gera e checa.
6. O conector assina e verifica com RSA-SHA1/256/384/512, ECDSA e
   algoritmo customizado, e detecta adulteração.
7. O núcleo não referencia o conector (teste de arquitetura).
8. O exemplo DPS + `infErp` → `webserviceNFSe` roda de ponta a ponta,
   com as duas assinaturas válidas.

## Fase 14 — Mudanças do XSD de destino e remapeamento

**Objetivo**: quando sai uma nova versão do XSD de destino (ex.: da
prefeitura), mostrar o que mudou e o que o mapeamento precisa, e permitir que
o operador remapeie sem perder nada em silêncio (ADR-0017). Pedido do usuário.

**Gate**:
1. No editor, sobre um DE-PARA já mapeado, o autor carrega a v2 do XSD de
   destino, com um campo mapeado renomeado e um campo novo obrigatório.
2. O editor mostra o painel "Mudanças do XSD": o que remapear (com sugestão),
   o que mapear, o que revisar e o que é informativo. Isso inclui mudança de
   tipo, restrição, cardinalidade e sequência de tags.
3. A publicação fica bloqueada enquanto houver mapeamento pendente.
4. "Mover para" a sugestão recupera o mapeamento.
5. Pela API, o XSD de um template publicado é comparado com a v2, com o
   mesmo resultado do editor.
6. Outra organização recebe 404, e a credencial de runtime não acessa.

**Status**: concluída em 2026-10-02.
- Cumprido pelo e2e (passo 13), por `Phase14GateTests`, `SchemaChangeTests`
  e pelos testes JS.
- Revisão independente: rodada 1 REPROVADO (C1); rodada 2 APROVADO COM
  RESSALVAS, com os avisos corrigidos e as dívidas registradas em
  PROJECT_STATE.

## Fase 13 — Contrato de entrada versionado com extensões

**Objetivo**: o autor monta no editor o modelo de entrada (XSD oficial +
blocos próprios adicionados na árvore), salva e versiona, e o reutiliza em
vários DE-PARAs (ADR-0016). Pedido do usuário (uso real: DPS + infErp).

**Gate**: no editor, a partir dos XSDs da DPS-exemplo, o autor adiciona um
bloco com campos (inclusive repetível), salva a versão 1, cria um template que
usa esse contrato, testa com XML contendo o bloco e publica; o XML de entrada
é transformado pela API; XML com o bloco fora das regras é recusado com
posição; a versão salva é imutável e reutilizável; outra organização não vê
o contrato.

**Status**: concluída em 2026-10-01.
- Cumprido pelo e2e no navegador (passos 11d' e 12 a 12d) e pelos testes
  `Phase13GateTests`, `SourceExtensionTests` e `SourceContractTests`.
- Revisão independente: rodada 1 REPROVADO; rodada 2 APROVADO COM RESSALVAS.
  As ressalvas R1, A1 e A2 foram corrigidas; A3, I2 e I3 ficaram registradas
  como dívida em PROJECT_STATE.

## Fase 12 — Contrato de origem XSD e entrada XML

**Objetivo**: realizar a ideia central do produto — o autor escolhe o XSD de
origem, o integrador envia um XML nesse formato e recebe o XML do XSD de
destino (ADR-0015). Fase acrescentada depois da Fase 11 a pedido do usuário.

**Entregas**: XSD de origem no snapshot e na camada base; forma da origem
derivada do XSD; leitura validada do XML de origem; geração a partir de XML
(API, SDK, fallback, pacote offline); cenários em XML nos gates; editor com
modo "XML (XSD de origem)".

**Gate**: um template criado no editor a partir de um XSD de origem e um XSD
de destino, testado com XML de exemplo e publicado, transforma XML de
origem em XML de destino pela API e pelo SDK; XML fora do XSD de origem é
recusado com posição; hashes de snapshots e camadas existentes não mudam.

## Fase 0 — Bootstrap de IA e governança

**Objetivo**: tornar o projeto retomável por qualquer nova sessão.

**Entregas**: estrutura `.claude/` (agentes, skills, rules); `CLAUDE.md`;
estado, roadmap, glossário e handoff; templates de ADR; convenções de código,
teste e documentação; backlog inicial por fases.

**Gate**: uma nova sessão consegue explicar o projeto, localizar o estado e
indicar a próxima tarefa somente lendo os arquivos persistidos.

## Fase 1 — Descoberta técnica e arquitetura executável

**Objetivo**: fechar contratos e riscos antes de codificar o motor.

**Entregas**: diagramas de contexto, containers e componentes; modelo de
domínio; ADRs de arquitetura, versionamento, persistência, cache e formato de
template; contratos públicos preliminares de SDK e API; threat model inicial;
fixtures representativas de XSD.

**Gate**: nenhuma dependência de infraestrutura invade domínio ou motor;
limites e formatos críticos estão documentados e revisados.

## Fase 2 — Vertical slice do motor

**Objetivo**: provar o fluxo ponta a ponta mais simples.

**Entregas**: importação de XSD único; árvore interna tipada; mapeamento
direto de campos escalares; geração ordenada de XML; validação por XSD; SDK
C# mínimo; testes unitários e exemplo realista.

**Gate**: um objeto de entrada gera XML válido sem strings concatenadas e com
diagnóstico útil.

## Fase 3 — XSD completo e contratos compostos

**Objetivo**: suportar layouts fiscais reais.

**Entregas**: ZIP, `include` e `import`; namespaces; tipos, restrições,
atributos e cardinalidade; `sequence`, `choice` e `all`; módulos de origem
versionados e namespaced; segurança de importação.

**Gate**: pacotes complexos são importados de forma determinística, segura e
reproduzível.

## Fase 4 — Motor de regras e coleções

**Objetivo**: implementar a flexibilidade do DE-PARA.

**Entregas**: catálogo tipado de operadores; grupos lógicos; conversões e
lookups; `for each`, filtros e listas aninhadas; extensão segura por novos
operadores C#; diagnóstico por regra e caminho.

**Gate**: cenários escalares e de coleções geram XML correto e nenhum código
arbitrário é executado.

## Fase 5 — API, MongoDB, cache e SDK público

**Objetivo**: disponibilizar o motor nos dois formatos oficiais.

**Entregas**: API ASP.NET Core; MongoDB por adaptadores; snapshots
compilados; cache e invalidação atômica; SDK empacotável; OpenAPI; testes de
integração.

**Gate**: API e SDK produzem resultados equivalentes; execução com cache
aquecido não acessa MongoDB.

## Fase 6 — Editor visual

**Objetivo**: permitir configuração por usuários autorizados.

**Entregas**: árvores de origem e destino; editor de mapeamentos; query
builder de regras; blocos de iteração; diagnósticos visuais; execução de
testes.

**Gate**: um usuário cria e testa um template sem editar JSON manualmente.

## Fase 7 — Camadas, versões e publicação

**Objetivo**: suportar produto, cliente e evolução segura.

**Entregas**: base, produto e cliente; diferenças rastreáveis; snapshot
imutável; atualização manual e automática; conflitos, rollback e fallback;
auditoria. (A publicação a partir do editor visual — adiada da Fase 6 —
passa para a Fase 9: publicar por HTTP exige a autorização por ação
`template.publish` que só existe na Fase 9; na Fase 7 a publicação é
feita pelo SDK/Application.)

**Gate**: uma atualização incompatível não altera a versão ativa; toda
decisão de resolução pode ser explicada.

## Fase 8 — Qualidade automatizada

**Objetivo**: impedir publicação de templates estrutural ou semanticamente
inválidos.

**Entregas**: gerador de dados sintéticos; testes estruturais automáticos;
testes funcionais obrigatórios; comparação semântica de XML; gate de
publicação; regressão em atualização de camadas.

**Gate**: não é possível publicar com teste obrigatório falhando.

## Fase 9 — Segurança, autorização e observabilidade

**Objetivo**: preparar uso multiempresa em produção.

**Entregas**: isolamento por tenant; permissões por ação; credencial runtime
somente uso; OpenTelemetry; auditoria; captura diagnóstica segura; testes de
segurança; endpoints HTTP de escrita (camadas, publicação, rollback) e
publicação a partir do editor visual somente de versões válidas (vindos
das Fases 6–7).

**Gate**: testes comprovam isolamento e ausência de payload fiscal em logs
normais.

## Fase 10 — Pacotes, RAG e documentação operacional

**Objetivo**: facilitar promoção, suporte e continuidade do conhecimento.

**Entregas**: importação e exportação portável; corpus documental; pipeline
incremental de indexação; controle de acesso na recuperação; avaliações do
RAG; runbooks; documentação para consumidores do SDK e API.

**Gate**: um pacote passa entre ambientes com hashes preservados; o RAG
recupera fontes corretas e informa versão, sem interferir no runtime
determinístico.

## Fase 11 — Performance, resiliência e prontidão

**Objetivo**: validar a operação empresarial.

**Entregas**: teste a 200 gerações/minuto; teste de margem a 2.000/minuto;
percentis, CPU, memória e cache; testes de concorrência e invalidação;
runbooks de falha e rollback; revisão final de arquitetura, segurança e
documentação.

**Gate**: metas atingidas ou gargalos medidos e aceitos formalmente; operação
e recuperação estão documentadas.

## Backlog detalhado — Fase 1 (próxima fase desbloqueada)

Objetivo da fase: fechar contratos e riscos antes de codificar o motor. Nada
aqui é implementação de produto — são decisões, diagramas e fixtures.

1. **Diagramas de arquitetura** (agente `architect`)
   - C4 Nível 1 (contexto do sistema) em `docs/architecture/SYSTEM_CONTEXT.md`:
     atores (usuário configurador, consumidor via SDK, consumidor via API,
     conector externo de emissão) e limites do sistema.
   - C4 Nível 2 (containers) em `docs/architecture/CONTAINERS.md`: mapear os
     projetos alvo de `docs/requirements/PROMPT_MESTRE_CLAUDE.md` seção 8
     para containers lógicos (motor, API, SDK, editor web, persistência).
   - C4 Nível 3 (componentes) em `docs/architecture/COMPONENTS.md`, ao menos
     para o motor de geração/validação.

2. **Modelo de domínio** (agentes `architect` + `domain-xsd`)
   - Modelar as entidades da seção 9 do requisito de origem
     (organização/tenant, contrato de origem, módulo de origem, versão de
     módulo, schema de destino, fornecedor, município, operação, template,
     versão de template, camada de produto, camada de cliente, mapeamento,
     grupo de regras, operador, bloco de iteração, caso de teste, publicação,
     snapshot compilado, política de atualização, política de fallback,
     relatório de validação, evento de auditoria) em `docs/domain/`.
   - Deixar "operação" explicitamente extensível (não enum fechado), com
     modelos iniciais para emissão, consulta, cancelamento e cancelamento por
     substituição.

3. **ADRs obrigatórios da fase** (agente `architect`, skill `architecture-adr`)
   - ADR de arquitetura geral (Clean Architecture + DDD pragmático aplicado,
     monólito modular, runtime sem estado).
   - ADR de versão .NET/ASP.NET Core LTS adotada.
   - ADR de versionamento (contratos, schemas, templates, camadas, snapshots
     imutáveis).
   - ADR de persistência (MongoDB por adaptadores — formato de documento para
     snapshot compilado).
   - ADR de estratégia de cache e invalidação atômica.
   - ADR de formato de template/camada (como base, produto e cliente são
     representados e resolvidos).

4. **Contratos públicos preliminares** (agente `api-sdk`)
   - Esboço das fachadas de SDK/API listadas na seção 26 do requisito de
     origem (sem implementação ainda) em `docs/api/`.
   - Formato do relatório estruturado de resultado de geração (seção 18).

5. **Threat model inicial** (agente `security-observability`, skill
   `security-review`)
   - Cobrir multiempresa, upload de XSD, permissões por ação e diagnóstico
     sensível, em `docs/architecture/` ou `docs/operations/`.

6. **Fixtures representativas de XSD** (agente `domain-xsd`, skill
   `xsd-contract-analysis`)
   - Escolher 1–2 XSDs de referência (um simples, um com `choice`/coleções)
     para servir de base à Fase 2, salvos em `docs/schemas/`.

7. **Revisão independente da Fase 1** (agente `reviewer`)
   - Confirmar o gate: nenhuma dependência de infraestrutura invade
     domínio/motor nos diagramas e ADRs; limites e formatos críticos
     documentados e revisados.

Critério de entrada da Fase 1: Fase 0 com gate cumprido (ver
`docs/PROJECT_STATE.md`).

## Backlog detalhado — Fase 6 (editor visual)

Decisões de base (registradas em ADR-0009 ao fim da fase): editor em HTML,
CSS e JavaScript simples (seção 7 do requisito de origem), módulos ES sem
build nem dependências npm; `Tradux.Web` é um host estático que fala
**só HTTP** com a API (CONTAINERS.md); endpoints de desenho são sem estado
(não tocam MongoDB). Publicação pelo editor fica para a Fase 7 (camadas e
publicação) e só ganha endpoint HTTP com autorização (Fase 9).

1. Domínio: caminhos com ponto (`Tomador.Cnpj`) navegam objetos aninhados
   na `SourceDataTree` (JSON e objeto C#), distinguindo ausente de nulo.
2. Motor: metadados do catálogo de operadores (categoria, descrição,
   parâmetros, tipo de resultado) para o editor listar — sem mudar o
   registro nem permitir código arbitrário.
3. Contratos + Application: descrição da árvore de destino a partir de XSD
   enviado (em memória, mesmas proteções de importação); teste de rascunho
   (compila, lista obrigatórios sem mapeamento/erros de tipo e
   cardinalidade, gera com dados de exemplo, compara com XML esperado
   semanticamente).
4. API: `POST /v1/design/schema`, `GET /v1/design/operators`,
   `POST /v1/design/test`; CORS configurável para a origem do editor (só
   em `/v1/design/*`);
   limite de tamanho do corpo (`Tradux:Design:MaxRequestBytes`, 413). SDK:
   mesmas fachadas (`TraduxDesigner`).
5. Web: árvore de origem (inferida dos dados de exemplo) e de destino;
   DE-PARA por seleção ou arraste; valor por campo/constante/transformação
   (operadores do catálogo, aninháveis)/omitir/nulo, com padrão; condição
   estilo query builder (E/OU/negação, grupos aninhados); bloco `for each`
   (coleção, filtro, ordenação, listas aninhadas); marcação visual de
   obrigatórios sem mapeamento e erros; teste com dados de exemplo e
   comparação de XML; rascunho salvo no navegador e exportável.
6. Testes: Application/API dos endpoints de desenho; testes Node
   (`node --test`) dos módulos JS puros (montagem do `MappingTemplate`,
   query builder, inferência da árvore de origem); teste de arquitetura
   "Web não referencia projeto .NET interno"; verificação manual do fluxo
   no navegador (criar → testar).

**Gate**: um usuário cria e testa um template sem editar JSON
manualmente — verificado no navegador de ponta a ponta.

## Backlog detalhado — Fase 7 (camadas, versões e publicação)

Base: seções 16–17 do requisito de origem, ADR-0003/0004/0005/0006.
Decisões novas ficam no ADR-0010.

1. Domínio: camadas `Base`/`Product`/`Client` versionadas e imutáveis
   (`TemplateLayer`), com overrides `Set`/`Remove` endereçados pelo caminho
   lógico do nó de destino (mesmo caminho do editor, `/DPS/numero`); base
   traz o XSD de destino e o conjunto completo; produto e cliente guardam
   só diferenças; tabelas DE-PARA também por override. Camada de outra
   organização só pode ser pai se for compartilhada.
2. Resolução explicável: base → produto → cliente, com histórico por
   caminho (qual camada definiu, substituiu ou removeu cada regra) e
   conflitos (caminho inexistente no XSD, tipo de nó incompatível,
   conteúdo não mapeável, remoção de algo inexistente). Conversão
   `MappingTemplate` ⇄ mapa por caminho usando o schema (mesma regra de
   emissão de complexo obrigatório do editor).
3. Publicação por camadas: resolve → conflitos bloqueiam → gates
   (compilação + cenários funcionais do vínculo; ponto de extensão para os
   gates da Fase 8) → snapshot com manifesto de camadas e proveniência.
   Falha nunca toca a versão ativa.
4. Vínculo do template (`TemplateBinding`): cadeia de camadas, política de
   atualização (manual/automática) e cenários funcionais. Nova versão de
   camada propaga para os vínculos afetados: automática rebaseia a camada
   cliente, resolve, testa e publica só se tudo passar; manual gera
   proposta com diferenças e conflitos, aplicada depois por aprovação.
   Diferenças rastreáveis entre a resolução ativa e a proposta.
5. Rollback para uma versão publicada anterior (transacional, invalida
   cache pelo Change Stream). Fallback de execução: lista ordenada de
   versões candidatas no pedido; primeira válida vence; erros agrupados
   por versão.
6. Auditoria de criação de camada, publicação, rollback e atualização
   (aplicada, rejeitada, proposta) — sem payload fiscal.
7. MongoDB: `templateLayers`, `templateBindings`, `templateUpdates`,
   `auditEvents` + manifesto/proveniência no snapshot. SDK: fachada de
   administração. API: somente leitura (explicação da resolução, versões);
   escrita por HTTP na Fase 9.
8. Testes: unidade (resolução, conflitos, diff, propagação, fallback,
   rollback) e integração com MongoDB real, incluindo o gate: atualização
   incompatível (conflito e cenário reprovado) não altera a versão ativa e
   a explicação cobre todas as regras publicadas.

**Gate**: uma atualização incompatível não altera a versão ativa; toda
decisão de resolução pode ser explicada.

## Backlog detalhado — Fase 8 (qualidade automatizada)

Base: seção 19 do requisito de origem, `.claude/rules/testing.md`,
ADR-0010 (ponto de extensão `IPublicationGate`). Decisões no ADR-0011.

1. XSD: facetas (`length`/`minLength`/`maxLength`, enumeração, pattern,
   limites, dígitos) no modelo interno, extraídas do schema compilado.
2. Perfil de origem inferido do template: campos por escopo (raiz e item de
   coleção), destinos diretos com facetas, dicas de tipo pelos operadores,
   tabelas DE-PARA usadas como chave.
3. Gerador de dados sintéticos determinístico: obrigatórios; completo;
   limites mínimo e máximo; cada valor de enumeração; valores que casam com
   patterns; repetições (vazia/várias); nulo separado de ausente; choice
   registrado como não aplicável enquanto o motor não o mapeia; um inválido
   esperado por restrição crítica.
4. Testes estruturais automáticos: execução da suíte contra o template
   compilado, relatório por categoria sem valores, gate
   `STRUCTURAL_TEST_FAILED`.
5. Testes funcionais obrigatórios: asserções estruturais por caminho
   (`Exists`/`Absent`/`Equals`/`Count`); publicação exige ao menos um
   cenário com XML esperado ou asserções. Comparação semântica de XML
   coberta por testes próprios (prefixo, whitespace, ordem de atributos,
   comentários).
6. Gate único de publicação: pipeline compilação → estrutural → funcional
   para o caminho por camadas e para o publicador do SDK (fecha a dívida W1
   da Fase 7); `SnapshotPublisher` interno.
7. Regressão em atualização de camadas: pipeline completo na proposta +
   mudança de saída por cenário funcional registrada na atualização.
8. Endpoint de desenho `POST /v1/design/quality` e painel "Qualidade" no
   editor (relatório da suíte estrutural do rascunho).
9. Testes: facetas, perfilador, gerador de pattern, suíte, gate (camadas e
   SDK direto), regressão, endpoint (integração), módulo JS do painel.

**Gate**: não é possível publicar com teste obrigatório falhando.

## Backlog detalhado — Fase 9 (segurança, autorização e observabilidade)

Base: seções 20 e 23 do requisito, `.claude/rules/security.md`. Decisões
no ADR-0012.

1. Modelo de acesso: permissões da seção 20, papéis (`admin`, `runtime`,
   `editor`, `auditor`), principal (organização, ator, permissões),
   credenciais por chave de API com hash, emissão/revogação auditadas
   (SDK `TraduxAccess`), credenciais iniciais por configuração.
2. API: esquema de autenticação `ApiKey`, política por permissão em toda
   rota `/v1`, isolamento por organização (404 para outra organização),
   ator = principal autenticado; CORS do editor com cabeçalho de credencial.
3. Escrita por HTTP: camadas, vínculo, publicação por camadas, publicação
   de conteúdo (editor), configuração de vínculo, rollback,
   aprovação/rejeição; leitura da auditoria; auditoria do teste de desenho.
4. Dívidas: transição condicional de status de proposta (A6), rollback
   condicionado à versão ativa (W3), limite da lista de fallback (W7),
   importação de XSD recusa pattern com quantificador acima do limite ou
   ilimitado aninhado.
5. Observabilidade: `TraduxTelemetry` (spans e métricas de geração, cache e
   publicação), log estruturado por geração sem payload, OpenTelemetry na
   API (OTLP opcional).
6. Captura diagnóstica: ligar/ler/apagar com `diagnostic.capture`,
   mascaramento, AES-GCM, TTL, auditoria, estado em memória (caminho
   aquecido sem MongoDB).
7. Editor: publicar o rascunho (organização/template/operação, credencial
   na sessão, cenário do autor a partir de dados de exemplo + XML
   esperado), só versões que passam nos gates.
8. Testes: matriz de permissões por rota (nenhuma rota sem política),
   isolamento em toda rota com organização, credencial de runtime só gera,
   varredura de logs/traces/métricas sem valor fiscal, captura diagnóstica
   (mascarada, criptografada em repouso, expira, auditada), dívidas.

**Gate**: testes comprovam isolamento e ausência de payload fiscal em logs
normais.

## Backlog detalhado — Fase 10 (pacotes, RAG e documentação operacional)

Base: seções 22 e 24 do requisito de origem. Decisões no ADR-0013.

1. Snapshot guarda os cenários funcionais com que foi publicado (fora do
   hash); repositórios de camada ganham importação de versão exata.
2. Pacote `.traduxpkg`: exportação (manifesto com hash por entrada, conteúdo
   do snapshot, cenários, camadas exatas, vínculo) com validação de
   segredos antes de exportar.
3. Importação: integridade, compatibilidade, limites, caminhos, segredos;
   camadas na versão exata (conflito se divergente); publicação pelos
   gates; conferência do hash do snapshot; auditoria.
4. Superfícies: API (`template.export`/`template.import`), SDK
   `TraduxPackages` e `TraduxPackageRuntime` (execução offline, sem MongoDB).
5. RAG: projeto `Tradux.Knowledge` (porta de índice, BM25 inicial,
   chunking por tipo, metadados da seção 24, pipeline incremental por hash,
   remoção de obsoletos, escopo de tenant/nível de acesso), registro
   `docs/rag/sources.json`, catálogo de operadores como fonte.
6. Busca por API (`GET /v1/knowledge/search`, `template.read`, escopo da
   credencial) com fonte e versão em todo resultado; teste de arquitetura:
   núcleo e `Application` não referenciam o RAG.
7. Avaliações: `docs/rag/eval-set.json`, precisão@k e cobertura com limiares.
8. Runbooks e guia do consumidor (SDK/API); docs do RAG atualizados.
9. Testes: pacote ida e volta entre dois ambientes (bancos distintos) com
   hashes iguais, recusa de pacote adulterado/com segredo/caminho perigoso,
   conflito de camada, execução offline equivalente à API, RAG
   (incremental, remoção, isolamento, avaliação).

**Gate**: um pacote passa entre ambientes com hashes preservados; o RAG
recupera fontes corretas e informa versão, sem interferir no runtime
determinístico.


## Backlog detalhado — Fase 11 (performance, resiliência e prontidão)

Base: seção 25 e Fase 11 do requisito de origem. Decisões no ADR-0014.

1. Limites de entrada da geração (`GenerationLimits`: profundidade, nós,
   texto) no ponto único `XmlGenerationService` (API e SDK iguais); corpo
   de `POST …/xml` limitado (413); configuração `Tradux:Generation`.
2. Resiliência: `ServerSelectionTimeout` curto e configurável; MongoDB
   indisponível → `503` com `Retry-After`; `/health/live` e `/health/ready`
   (MongoDB fora = `Degraded`).
3. Infraestrutura de carga nos testes de integração: carga em modelo aberto
   sobre Kestrel real, relatório com percentis, CPU, memória, cache e
   comandos do MongoDB; saída JSON em `artifacts/perf/`.
4. Testes de carga: 200/min e 2.000/min (janela curta na suíte, completa
   por variável de ambiente), saturação em modelo fechado; metas do ADR-0014.
   (Ajuste 2026-10-01: com menos de 100 amostras a janela curta confere p95 —
   o p99 de 50 amostras é o próprio máximo; a execução registrada de 60 s,
   com 200+ amostras, confere o p99 das metas.)
5. Benchmarks separados de compilação e execução.
6. Concorrência e invalidação: troca de versão sob carga em duas instâncias
   (publicação e rollback), respostas sempre de uma única versão,
   convergência; geração aquecida com MongoDB derrubado; chave fria falha rápido.
7. Documentação: `docs/operations/PERFORMANCE.md` (resultados medidos,
   capacidade, gargalos aceitos), runbook de falhas e recuperação,
   atualização de incidentes/rollback/instalação e do contrato da API.
8. Revisão final (agente `reviewer`) de arquitetura, segurança e
   documentação do sistema inteiro, além do gate da fase.

**Gate**: metas atingidas ou gargalos medidos e aceitos formalmente;
operação e recuperação documentadas.


## Backlog detalhado — Fase 12 (contrato de origem XSD e entrada XML)

Base: pedido do usuário (XML de origem → XML de destino é o fluxo central) e
seção 10 do requisito de origem. Decisões no ADR-0015.

1. Xsd: importação segura do XSD de origem sem as restrições do destino;
   `SourceShape` (objeto/lista/valor, atributos `@`, `$value`, tipos) e
   `SourceXmlReader` (validação, limites, codificação declarada, posições).
2. Application: `SnapshotContent.SourceSchema` (hash só quando presente),
   `CompiledSnapshot.Source`, `XmlGenerationService.TransformAsync`
   (inclusive fallback por versão), cenários com `SourceXml` nos gates e na
   regressão de atualizações; camada base com XSD de origem.
3. Persistência e pacote: MongoDB (snapshot e camada) e `.traduxpkg`.
4. API: `POST …/transform`, `POST /v1/design/source-schema`,
   `sourceSchema`/`sourceXml` em teste de rascunho, publicação e camada;
   limite de corpo pelo endpoint.
5. SDK: `TransformXmlAsync`, `DescribeSourceSchema`.
6. Editor: modo "XML (XSD de origem)" como padrão; e2e no navegador cobrindo
   o fluxo XML → XML até a API.
7. Testes (Xsd, Application, integração com MongoDB, JS do editor, e2e) e
   documentação (ADR-0015, contrato da API, guia do consumidor, fixtures).

**Gate**: ver "Fase 12" acima.

## Backlog detalhado — Fase 13 (contrato de entrada versionado com extensões)

Base: pedido do usuário; DOMAIN_MODEL §3.2–§4. Decisões no ADR-0016.

1. Xsd: modelo de extensão, forma combinada (base + extensões) com
   validação de caminho/nome, leitor que valida extensões pelas regras e o
   restante pelo XSD.
2. Application: SourceSchemaContent com extensões e referência ao contrato
   (hash só quando houver); SourceContractService (versões imutáveis, dedupe
   por hash, auditoria) e porta de repositório.
3. MongoDB: coleção sourceContracts (índice único org+id+versão).
4. API: CRUD de contrato (ler/criar versão), /v1/design/source-contract,
   extensões no teste de rascunho e na publicação. SDK: TraduxSourceContracts.
5. Editor: contrato de entrada (abrir/criar, + campo/+ bloco, editar/remover,
   salvar versão) e uso no template.
6. Testes (Xsd, Application, integração, JS, e2e) e documentação.

**Gate**: ver "Fase 13" acima.

## Backlog detalhado — Fase 14 (mudanças do XSD de destino e remapeamento)

Base: pedido do usuário. Decisões no ADR-0017.

1. Application: `SchemaChangeAnalyzer` compara dois XSDs pelo modelo
   canônico. Faz o alinhamento por caminho, detecta elementos movidos e
   calcula as mudanças, o impacto e as sugestões.
2. Contracts: `SchemaChangeKind`, `SchemaChangeImpact`, `SchemaChange`,
   `SchemaChangeReport` e os pedidos.
3. API: `/v1/design/schema-changes` e `…/templates/…/schema-changes`. SDK:
   `CompareSchemas` e `CompareSchemaWithActiveAsync`.
4. Editor:
   - comparar antes de trocar o XSD;
   - mapeamentos pendentes com mover/descartar;
   - painel "Mudanças do XSD" e marcas na árvore;
   - publicação bloqueada enquanto houver pendente.
5. Testes (Application, integração, JS e e2e passo 13) e documentação.

**Gate**: ver "Fase 14" acima.

## Backlog detalhado — Fase 15 (choice, eventos, conversor gerado e assinatura)

Base: pedido do usuário. Decisões nos ADR-0018 e ADR-0019.

1. Motor:
   - choice/all e grupos aninhados no destino;
   - eventos (`GenerationEvents`);
   - `xs:dateTime`;
   - coleção sobre objeto único e `$root`.
2. Application:
   - árvore de destino com grupos transparentes e `ContentRepeatable`;
   - `MappingPaths`;
   - perfil e suíte estrutural com choice e objeto único;
   - `CSharpConverterGenerator`.
3. SDK: `GeneratedTemplateGuard`, `TemplateVersionPolicy` e
   `GenerateConverterCodeAsync`. API: `GET …/codegen/csharp`. Ferramenta:
   `tools/Tradux.CodeGen`.
4. Editor: grupos transparentes e status de choice obrigatório.
5. Conector externo `connectors/Tradux.Connectors.XmlSignature` e testes; regra
   de arquitetura.
6. Exemplo fora do repositório:
   `<pasta-dev>\exemplos\dps-erp-webservice`.
7. Testes (motor, aplicação com o código gerado compilado via Roslyn,
   conector, arquitetura, JS) e documentação.

**Gate**: ver "Fase 15" acima.

## Backlog detalhado — Fase 16 (saneamento)

Base: auto-revisão da Fase 15 e pedido do usuário. Decisão no ADR-0020.

1. Domínio: `FieldScope`, `CollectionMapping.SingleObjectAsItem` e
   `SourceDataTree.Root`.
2. Motor:
   - escopo raiz no compilador, e `$root.` como erro;
   - objeto como item só com a opção;
   - eventos explícitos;
   - `RuleExecutor.ContentWithoutData`.
3. Aplicação:
   - eventos explícitos no `XmlGenerationService`;
   - perfil com `BranchUse`/`AlwaysPresent` e `BranchPlan`;
   - conversor gerado com raiz tipada e eventos explícitos.
4. SDK: sobrecargas com eventos; `GeneratedTemplateGuard.Version`.
5. Conector:
   - `SigningKey`, `DelegatedRsa` e `DelegatedEcdsa`;
   - `Inspect`/`SignedElements`;
   - Id ambíguo e chave/certificado.
6. Editor: `scope: 'root'`, `singleObject` e categoria `Choice`.
7. Exemplo migrado e testes.

**Gate**: ver "Fase 16" acima.

## Backlog detalhado — Fase 17 (regras em C# no conversor)

1. Motor: `TemplateCodeRules`, `TemplateCompiler.Compile(…, codeRules)` com
   `CODE_RULE_MISSING`/`CODE_RULE_UNUSED`, e `Truthy` público.
2. Application:
   - `SnapshotCompiler(codeRules)`;
   - gerador com `{P}Regras`, `{P}Tabelas` e `{P}Conteudo`;
   - conversor local.
3. SDK: `EmbeddedTemplate`.
4. Testes de equivalência e exemplo sem banco.

**Gate**: ver "Fase 17" acima.
