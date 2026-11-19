# ADR-0012: Segurança, autorização por ação, observabilidade e captura diagnóstica

> Atualização (Fase 11): com o MongoDB indisponível para uma instância, a
> revogação pode levar até validade + carência (`CredentialStaleGrace`,
> 10 min) nela — ver ADR-0014 §4.

- **Status**: aceito
- **Data**: 2026-09-30
- **Fase**: 9 — Segurança, autorização e observabilidade

## Contexto

Seções 20 e 23 do requisito e `.claude/rules/security.md`: isolamento por
organização; permissões por ação (`template.read|create|edit|test|publish|
use|archive|import|export`, `diagnostic.capture`, `audit.read`); papel
administrador inicial sem booleano `IsAdmin`; credencial de runtime só com
`template.use`; auditoria de criação, alteração, teste, publicação,
rollback, importação, exportação, permissão e diagnóstico sensível; logs,
métricas e traces OpenTelemetry com organização, operação, template,
versão, duração, cache, versões tentadas, fallback, erros, resultado e
correlação — sem payload nem XML; captura diagnóstica temporária, opt-in,
com permissão, mascaramento, criptografia, retenção curta, auditoria e
exclusão automática. Gate: "testes comprovam isolamento e ausência de
payload fiscal em logs normais".

Até aqui a API não autenticava (organização vinha da rota e era
confiada); escrita só pelo SDK. A Fase 9 também herda: escrita por HTTP e
publicação pelo editor (Fases 6–7), e dívidas da revisão da Fase 7 (A6
transição condicional de proposta, W3 rollback sem concorrência, W7 lista
de fallback sem limite) e da Fase 8 (pattern com retrocesso/quantificador
enorme no validador XSD do .NET, que não tem timeout).

## Decisão

1. **Credencial por chave de API** (esquema `ApiKey`): cabeçalho
   `Authorization: ApiKey <segredo>` (ou `X-Api-Key`). O segredo é aleatório
   (32 bytes, Base64Url, prefixo `tradux_`), mostrado uma única vez; o
   servidor guarda só o SHA-256 (`apiCredentials`, índice único). Cada
   credencial pertence a **uma organização** e carrega **papéis** que se
   expandem em permissões. É um esquema de autenticação do ASP.NET Core:
   um provedor de identidade externo (JWT/OIDC) pode ser acrescentado
   depois produzindo as mesmas claims, sem mudar a autorização.
   Justificativa: o requisito não define provedor de identidade; chave de
   API é autocontida, testável e é a forma natural para credencial de
   runtime (máquina a máquina).
2. **Permissões e papéis** (`Application/Security`): as 11 permissões da
   seção 20 como constantes; papéis são conjuntos nomeados —
   `admin` (todas), `runtime` (só `template.use`), `editor`
   (`template.read|create|edit|test`), `auditor` (`template.read`,
   `audit.read`). Nenhuma verificação usa "é admin": toda rota exige uma
   permissão específica (política `perm:<permissão>`).
3. **Isolamento por organização**: rotas com `{organizationId}` só aceitam
   principal da mesma organização; outra organização recebe **404** (não
   revela existência). Rotas sem organização (desenho, sem estado) usam a
   organização do principal para auditoria. Camadas compartilhadas de
   outra organização continuam acessíveis só como pai/topo (ADR-0010) —
   leitura direta de camada alheia não existe por HTTP.
4. **Matriz de permissões** (toda rota `/v1` exige autenticação; teste de
   arquitetura garante que nenhuma rota fique sem política):
   - gerar XML → `template.use`;
   - versões, explicação, atualizações → `template.read`;
   - desenho: operadores/schema → `template.edit`; teste/qualidade →
     `template.test` (auditado como `template.test`);
   - criar versão de camada → `template.edit` (base/produto novos:
     `template.create`);
   - vincular e publicar, publicar, publicar conteúdo (editor), rollback,
     aprovar/rejeitar atualização → `template.publish`;
   - configurar vínculo → `template.edit`;
   - auditoria → `audit.read`;
   - captura diagnóstica (ligar, ler, apagar) → `diagnostic.capture`.
   `template.archive|import|export` ficam para a Fase 10 (pacotes).
   Camada compartilhada (`shared=true`, visível a todas as organizações)
   exige também `template.publish`. A garantia "nenhuma rota sem política"
   é um teste de integração que percorre todas as rotas `/v1`.
5. **Gestão de credenciais fora do HTTP nesta fase**: emitir/listar/revogar
   pelo SDK (`TraduxAccess`, sobre `CredentialService`; revogar uma
   credencial inicial vale para sempre — recolocá-la na configuração não a
   reativa) e credenciais iniciais por configuração
   (`Tradux:Security:BootstrapCredentials`: organização, nome, papéis e
   **hash** do segredo — nunca o segredo). Emissão e revogação geram evento
   de auditoria `permission.grant`/`permission.revoke`. Não há permissão
   `credential.manage` na seção 20; expor gestão por HTTP exigiria criá-la.
6. **Escrita por HTTP** (vinda das Fases 6–7): camadas, vínculo, publicação
   por camadas, publicação de conteúdo (editor), configuração de vínculo,
   rollback, aprovação/rejeição, auditoria. O ator é sempre o principal
   autenticado (nunca um campo do corpo). Toda publicação continua passando
   pelo pipeline fixo de gates (ADR-0011) — "publicação pelo editor somente
   de versões válidas".
7. **Observabilidade**: `TraduxTelemetry` (`ActivitySource`/`Meter`
   "Tradux", da BCL — sem pacote no núcleo) instrumenta geração
   (span com organização, template, operação, versão usada, versões
   tentadas, fallback, resultado, códigos de erro, correlação, duração),
   cache (acerto/falha) e publicação. A API registra um log estruturado
   por geração com os mesmos campos e liga OpenTelemetry (traces, métricas,
   logs; exportador OTLP só se configurado). **Nunca** entram em log,
   tag ou métrica: dados de entrada, XML, valores de diagnóstico
   (mensagens de diagnóstico podem citar valor — só o código vai para
   telemetria), corpo de requisição.
8. **Captura diagnóstica** (seção 23): ligada por chave de template e
   duração (padrão 30 min, máximo 24 h) por quem tem `diagnostic.capture`;
   enquanto ativa, cada geração daquela chave grava entrada JSON e XML
   **mascarados** (campos com nome sensível — CPF/CNPJ, e-mail, telefone,
   nome, endereço, inscrição… lista configurável — viram `***` + 2 últimos
   caracteres), **criptografados** (AES-GCM, chave de configuração; sem
   chave configurada a captura não pode ser ligada) e com expiração em
   índice TTL do MongoDB (exclusão automática; retenção padrão 24 h, máximo
   72 h). Ligar, ler e apagar são auditados. O estado "captura ativa" é
   mantido em memória por processo e renovado periodicamente — o caminho
   aquecido de geração continua sem consultar o MongoDB. A criptografia
   fica na infraestrutura (armazenamento de diagnóstico), nunca no núcleo,
   e não é criptografia de documento fiscal emitido.
9a. **Timeout de regex em duas camadas** (revisão da Fase 9, B1): o host
   fixa o timeout padrão do processo (`RegexTimeout.Configure()`, 1 s,
   primeira linha do `Program`; quem hospeda o SDK deve fazer o mesmo) e o
   motor pré-checa cada valor com pattern com timeout explícito
   (`PatternGuard`) — estourou, a geração falha com `XSD_VALIDATION_TIMEOUT`
   sem chegar ao validador. A importação recusa repetição (máximo > 1)
   sobre grupo com quantificador variável; alternativas sobrepostas
   (`(a|aa)+`) ficam por conta do timeout.
9. **Dívidas herdadas**: aprovação/rejeição com transição condicional de
   status (`UpdateStatusAsync(expected → novo)`), rollback condicionado à
   versão ativa lida, lista de fallback limitada (`Tradux:Generation:MaxCandidateVersions`,
   padrão 5 → 400), e importação de XSD recusa pattern com quantificador
   acima do limite (padrão 1000) ou com quantificador ilimitado aninhado em
   grupo quantificado ilimitado (`(a*)*`, `(a+)+`) — o validador XSD do
   .NET não tem timeout de regex e roda em toda geração.

## Alternativas consideradas

- **JWT/OIDC agora**: exigiria escolher provedor sem requisito que o
  defina; a autorização por claims permite acrescentá-lo depois. Adiada.
- **Gestão de credenciais por HTTP**: exigiria permissão fora da lista da
  seção 20. Adiada; SDK + configuração cobrem o uso inicial.
- **403 para outra organização**: revela que o recurso existe. Rejeitada
  em favor de 404.
- **Log do corpo em falha para facilitar suporte**: é exatamente o que a
  captura diagnóstica opt-in resolve com controle. Rejeitada.

## Consequências

- Ator de auditoria: `credential:<nome>#<prefixo do id>` (nomes podem se repetir).
- Versão NÃO compartilhada de camada não gera atualização nem auditoria em
  vínculos de outra organização. Limite conhecido: a numeração é
  sequencial por camada, então quem consome vê `@1` e depois `@3` e pode
  deduzir que houve uma versão intermediária — sem conteúdo nem pais.
- Mensagens de camada inacessível são iguais para "não existe" e "não
  compartilhada" (mesmo código `LAYER_ACCESS_DENIED`).
- `correlationId` vai para log/trace: identificador opaco, até 64 caracteres
  `[A-Za-z0-9._:-]` — não usar dado fiscal como correlação.
- Limites conhecidos: sem limitação de taxa de autenticação (cache negativo
  só evita repetir consulta; limitação fica no gateway — Fase 11); limite
  de registros da captura é por processo; proposta pode ficar em
  `Applying` se o processo cair no meio da aprovação (a próxima publicação
  ou rollback segue normalmente; a proposta fica registrada assim);
  rotação da chave da captura torna ilegíveis (ignorados) os registros
  antigos até expirarem.

- Todo cliente da API precisa de credencial; testes e editor passam a
  enviá-la. Mudança de contrato em relação à Fase 8 (API sem autenticação).
- O editor ganha "Publicar" (com credencial informada pelo usuário, guardada
  só na sessão do navegador).
- Gate comprovado por testes: matriz de permissões por rota, isolamento
  entre organizações em cada rota com organização, credencial de runtime
  só gera, e varredura de logs/tags/métricas sem valores fiscais.
