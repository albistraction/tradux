# CLAUDE.md — Tradux (tradutor de XML por DE-PARA)

Este arquivo é a regra permanente do projeto. Mantenha-o curto. Conhecimento
especializado vai em `.claude/skills/`, `.claude/rules/` e `docs/`.

## O que é este projeto

Microsserviço multiempresa tradutor de XML (ADR-0025): o autor cadastra o
DE-PARA (XSD de origem → XSD de destino) no editor, o serviço versiona, e o
integrador envia o XML de origem para `POST /v1/templates/{nome}/convert` e
recebe o XML convertido. Núcleo em C#; entrega única: API HTTP (ASP.NET Core)
+ editor. Não há SDK/DLL para integrar.

Requisito de origem completo: `docs/requirements/PROMPT_MESTRE_CLAUDE.md`.
Não reinterprete esse documento de memória — releia-o quando a dúvida for sobre
escopo, limites ou critérios de aceite.

## Regra de execução obrigatória, toda sessão

0. Ao assumir a sessão: confira o estado real do repositório (`git status`,
   `git log`) contra o que `docs/PROJECT_STATE.md` e `handoffs/CURRENT.md`
   afirmam. Se houver divergência — arquivo que o estado diz existir mas não
   existe, teste que o estado diz que passou mas não roda, etc. — corrija a
   documentação antes de continuar. Nunca confie em uma afirmação de estado
   sem checar a evidência correspondente no repositório.
1. Leia este arquivo.
2. Leia `docs/PROJECT_STATE.md`.
3. Leia `docs/DECISIONS.md` e os ADRs da fase ativa em `docs/architecture/adrs/`.
4. Leia `docs/ROADMAP.md` e identifique a próxima tarefa desbloqueada.
5. Trabalhe apenas na fase ativa registrada em `docs/PROJECT_STATE.md`.
6. Use subagentes somente para trabalho independente, paralelizável ou que
   precise de contexto isolado. Nunca deixe dois agentes editarem o mesmo
   conjunto de arquivos ao mesmo tempo.
7. Integração, testes e revisão final são sempre feitos pelo agente
   coordenador, nunca delegados por completo.
8. Antes de encerrar a sessão, ou quando o contexto estiver ficando curto,
   atualize `docs/PROJECT_STATE.md` e `handoffs/CURRENT.md`.
9. Nunca declare uma fase concluída sem cumprir o gate de saída descrito em
   `docs/ROADMAP.md`.
10. Nunca implemente mais de uma fase por sessão sem necessidade explícita.

## Limites obrigatórios do núcleo

O motor gera e valida XML. Ele **não** implementa: chamadas REST/SOAP a
prefeituras, autenticação em fornecedores, orquestração de emissão,
polling/consulta de lote, assinatura digital, criptografia, ou armazenamento
de certificados/chaves. Essas responsabilidades pertencem a conectores
externos, fora deste repositório/módulo: quem chama o serviço pós-processa o
XML de resposta (ADR-0025).

O fallback interno escolhe uma versão que gera e valida localmente. Ele não
descobre o que um endpoint remoto aceita.

## Arquitetura essencial

- Fluxo central: XML de origem (XSD de origem escolhido pelo autor) → XML
  do XSD de destino (ADR-0015). JSON é entrada alternativa (`POST …/xml`).
- Entrada = XSD de origem do template (ADR-0015/0025). O motor ainda lê extensões
  de origem de snapshots antigos (ADR-0016), mas nada novo as grava.
- Nova versão do XSD de destino: relatório de mudanças e remapeamento guiado
  (remapear/mapear/revisar/informativo), nada some em silêncio (ADR-0017).
- Destino com xs:choice/all (grupos transparentes no mapeamento) (ADR-0018).
- Contratos explícitos, nunca convenção implícita (ADR-0020): escopo raiz é
  `FieldScope.Root`, objeto como item é `SingleObjectAsItem`.
- Só o microsserviço (ADR-0025): sem SDK, sem conversor C# gerado, sem
  netstandard2.0/.NET 4.8, sem conector de assinatura (quem chama assina), sem
  catálogo de contratos de origem. Rotas `/v1/templates…` com a organização da
  credencial; versões = snapshots do serviço (publicar/rollback atualizam o
  cache da instância). Testes usam os serviços de `Application` e as rotas HTTP
  diretamente, sem fachada própria.
  Não reintroduza DLL, codegen ou alvo netstandard sem novo ADR.
- Clean Architecture: domínio e motor (`Tradux.Domain`,
  `Tradux.Engine`) não dependem de ASP.NET Core, MongoDB ou frontend.
- Monólito modular inicialmente, runtime sem estado, escalável horizontalmente.
- MongoDB só por adaptadores de infraestrutura (`Tradux.Infrastructure.MongoDb`).
- Caminho normal de geração com cache aquecido não consulta MongoDB.
- A API só converte transporte ⇄ contrato e delega a `Application` — nunca
  duplique regra de geração nos endpoints.
- Estrutura de solução alvo e fases: ver `docs/ROADMAP.md` e
  `docs/architecture/CONTAINERS.md`.

## Comandos de build/teste

- Build: `dotnet build` (a partir da raiz do repositório; usa `Tradux.slnx` —
  formato de solução XML do SDK .NET moderno, criado por `dotnet new sln`).
- Testes: `dotnet test`.
- SDK .NET: fixado em `global.json` (10.0.400 confirmado no ambiente,
  ver ADR-0002). `Directory.Build.props` fixa `net10.0`/`Nullable`/`ImplicitUsings`
  para todos os projetos.
- Projetos atuais (Fase 22):
  `src/Tradux.{Domain,Xsd,Engine,Contracts,Application,Infrastructure.MongoDb,Api,Web,Knowledge}`,
  `tests/Tradux.{Domain,Xsd,Engine,Application,Knowledge}.Tests`,
  `tests/Tradux.Api.IntegrationTests` (sobe um `mongod` real via
  `EphemeralMongo`; a 1ª execução baixa o MongoDB — precisa de internet uma
  vez), `tests/Tradux.ArchitectureTests` (limites de dependência —
  falha aqui é bloqueio), `tests/Tradux.Web.Tests` (roda `node --test`
  nos módulos JS do editor — exige Node.js 20+ no PATH). Fixtures
  compartilhadas em `tests/Shared/`.
- Local: `dotnet run --project src/Tradux.Api --launch-profile http`
  (:5220, precisa de MongoDB replica set) + `dotnet run --project
  src/Tradux.Web --launch-profile http` (:5230). Toda rota `/v1` exige
  credencial (`Authorization: ApiKey …`; em Development: `tradux_dev-admin-org-a`).
  Gate no navegador: `node tests/Tradux.Web.Tests/e2e/golden-path.mjs`
  (Chrome/Edge headless).
- Contêineres: `docker compose -f deploy/docker-compose.yml --env-file deploy/.env up --build`
  (MongoDB replica set + API :8080 + editor :8081; ver `deploy/.env.example`).
- Carga (Fase 11): a suíte roda 200/min e 2.000/min em janela de 15 s;
  execução completa: `TRADUX_LOAD_SECONDS=60 dotnet test tests/Tradux.Api.IntegrationTests
  --filter "FullyQualifiedName~Phase11Performance"` (relatórios em `artifacts/perf/`,
  resultados em `docs/operations/PERFORMANCE.md`).
- Sempre rode `dotnet build` e `dotnet test` antes de reportar uma tarefa de
  código como concluída, e atualize esta seção e "Último resultado de
  testes" em `docs/PROJECT_STATE.md` com o resultado real.

## Onde procurar o quê

- Estado operacional atual: `docs/PROJECT_STATE.md`
- Roadmap e gates por fase: `docs/ROADMAP.md`
- Decisões estáveis: `docs/DECISIONS.md` e `docs/architecture/adrs/`
- Glossário de domínio: `docs/GLOSSARY.md`
- Handoff acionável entre sessões: `handoffs/CURRENT.md`
- Agentes especialistas: `.claude/agents/`
- Skills reutilizáveis: `.claude/skills/`
- Regras por área (dotnet, testes, segurança, documentação): `.claude/rules/`
