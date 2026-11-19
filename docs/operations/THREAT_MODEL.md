# Threat model inicial — Tradux — tradutor de XML por DE-PARA

> Status: threat model de descoberta (Fase 1). Não é auditoria de código —
> nenhum código-fonte existe ainda. O objetivo é fixar riscos, severidades e
> o controle de design mínimo que ADRs e modelo de domínio devem refletir
> **agora**, para que as mitigações concretas (Fases 3, 7 e 9) não exijam
> retrabalho estrutural.
>
> Fonte normativa: `docs/requirements/PROMPT_MESTRE_CLAUDE.md`, seções
> 9 (domínio), 11 (importação segura de XSD), 16 (camadas/publicação/
> rollback), 20 (segurança e autorização), 21 (persistência/cache) e 23
> (observabilidade e diagnóstico). Regras permanentes: `.claude/rules/
> security.md`. Checklist de revisão: `.claude/skills/security-review/
> SKILL.md`.

## 1. Escopo e metodologia

Escopo: o motor de tradução/validação de XML (núcleo C#), a API HTTP, o SDK,
o editor visual (fases futuras) e o pacote portátil de importação/exportação.
Fora de escopo (por limite obrigatório do núcleo, ver `CLAUDE.md`): chamadas
REST/SOAP a prefeituras, autenticação em fornecedores externos, assinatura
digital, criptografia de documento fiscal e armazenamento de certificados —
essas responsabilidades pertencem a conectores externos fora deste
repositório; ameaças específicas a eles não são modeladas aqui, mas a
**ausência** dessas responsabilidades dentro do núcleo é, ela mesma, um
controle de segurança (superfície de ataque reduzida) e é tratada como tal na
seção 6.

Metodologia: STRIDE aplicado por área funcional (não por componente técnico
ainda inexistente), priorizado pelas seis áreas pedidas para a Fase 1:
isolamento multiempresa, upload de XSD/pacote, autorização por ação,
observabilidade/diagnóstico, pacote portátil, e integridade de publicação/
rollback.

Escala de severidade: **crítico** (compromete confidencialidade/integridade
entre organizações ou permite execução/leitura não autorizada em produção),
**alto** (viola isolamento ou controle exigido, mas com pré-condição menos
trivial), **médio** (degradação, negação de serviço localizada, ou exposição
que exige acesso já privilegiado).

## 2. Ativos principais

| Ativo | Por que importa |
|---|---|
| Dados de configuração por organização (contrato de origem, template, versão de template, camada de produto/cliente, mapeamento, grupo de regras, snapshot compilado) | Propriedade intelectual de configuração fiscal do cliente; vazamento entre organizações é violação contratual e possivelmente legal (LGPD/dados de terceiros). |
| Payload de entrada e XML gerado (ex.: documento fiscal) | Dado fiscal, frequentemente com dados pessoais/comerciais (CPF/CNPJ, valores, descrição de serviço) — não é dado de configuração, é dado de operação do cliente do cliente. |
| Credenciais e permissões (API key, permissão por ação) | Controlam quem pode ler, alterar, publicar ou usar templates. |
| Pacote portátil de import/export | Veículo de promoção entre ambientes; se comprometido, compromete o ambiente de destino. |
| Snapshot publicado ativo em produção | É o que efetivamente gera XML fiscal; substituição indevida tem impacto direto no cliente final. |
| Logs, métricas, traces (OpenTelemetry) e capturas diagnósticas | Não devem se tornar um segundo canal de vazamento de dado fiscal ou de configuração sensível. |
| Eventos de auditoria | Precisam ser íntegros e completos para investigação; sua ausência ou lacuna é, em si, uma falha de segurança. |

## 3. Atores e limites de confiança

- **Usuário configurador** (via editor visual / API administrativa): confiança
  média, escopo restrito à sua organização, sujeito a permissões por ação.
- **Consumidor via SDK/API em produção** (credencial de runtime): confiança
  baixa por padrão — deve operar só com `template.use`.
- **Conector externo de emissão** (fora do repositório): não confiável para o
  núcleo; o núcleo só expõe pontos de extensão de pós-processamento, nunca
  aceita dele configuração de template ou permissão.
- **Operador de plataforma/SRE**: acesso a diagnóstico e auditoria, sujeito a
  `diagnostic.capture` e `audit.read`, nunca a um papel "admin geral" implícito.
- Limite de confiança principal: **fronteira de organização/tenant** — todo
  dado de configuração e todo payload/XML pertence a exatamente uma
  organização, e nenhum componente (cache, índice, log, pacote exportado)
  pode atravessar essa fronteira sem verificação explícita.

## 4. Tabela de ameaças por área

### 4.1 Isolamento multiempresa

| ID | Ativo | Ameaça (STRIDE) | Vetor | Severidade | Mitigação exigida | Fase de implementação | Controle de design a refletir agora |
|---|---|---|---|---|---|---|---|
| TM-01 | Template/contrato/snapshot de outra organização | Information Disclosure | Query de leitura (repositório MongoDB) sem filtro de `organizacaoId`, especialmente em joins/agregações ou em telas de diagnóstico/listagem administrativa | Crítico | Toda query de leitura e escrita filtra por organização no nível do repositório, não da UI/controller; nenhum repositório expõe método que aceite busca sem tenant obrigatório | Fase 9 (isolamento por tenant é entrega explícita), preparado desde Fase 5 (MongoDB por adaptadores) | ADR de persistência deve declarar `organizacaoId` como parte obrigatória da chave de índice composto (junto com fornecedor/município/operação/versão) para **todas** as coleções que guardam dado de organização, e o modelo de domínio deve tornar `OrganizacaoId` um campo não anulável em toda entidade raiz de agregado (contrato, template, camada, snapshot, publicação, caso de teste, evento de auditoria) |
| TM-02 | Snapshot compilado em cache | Information Disclosure / Tampering | Chave de cache não incluir organização (ou incluir de forma previsível/colidível), permitindo que um processo sirva snapshot de outra organização sob concorrência ou reuso de chave | Crítico | Chave de cache completa e determinística incluindo `organizacaoId` explicitamente (não apenas hash de template), com testes de concorrência que provem que nunca há hit cruzado | Fase 5 (estratégia de cache) e Fase 9 (teste de isolamento formal) | ADR de estratégia de cache deve especificar o formato exato da chave (não deixar "chave completa" implícito) incluindo organização como primeiro componente |
| TM-03 | Identificadores de recurso (template, contrato, snapshot) | Information Disclosure / Spoofing | IDs sequenciais ou previsíveis permitem enumeração cross-tenant mesmo que a query filtre por organização em alguns endpoints mas não em todos (IDOR) | Alto | IDs opacos (GUID/ULID) **e** verificação de posse de organização em toda operação que recebe um ID por parâmetro, não confiar em "o ID já implica a organização" | Fase 5 (API) e Fase 9 (testes de autorização) | Modelo de domínio deve usar identificadores opacos desde já (não IDs numéricos incrementais) para toda entidade referenciável por API/SDK |
| TM-04 | Dado de organização em índice de busca / RAG (Fase 10) | Information Disclosure | Corpus indexado sem controle de acesso por organização, retornando trecho de configuração de outra organização em uma resposta de apoio | Alto | Controle de acesso na recuperação do RAG restrito à organização do solicitante (seção 24/Fase 10) | Fase 10 | Anotar já no ADR de versionamento/formato de template que nenhum conteúdo específico de organização é elegível para indexação RAG por padrão — apenas fontes aprovadas e genéricas (requisitos, ADRs, glossário, exemplos aprovados) |
| TM-05 | Dado de organização em log/trace estruturado | Information Disclosure | Log estruturado inclui nome/identificador de cliente final ou de organização em texto livre, tornando logs centralizados um vetor de correlação cross-tenant para quem tem acesso amplo a `audit.read`/observabilidade mas não à organização específica | Médio | Campos estruturados com organização tratados como dado de controle de acesso também na camada de observabilidade — acesso a logs/traces filtrado por organização para quem não tem escopo global | Fase 9 | Nenhuma edição necessária agora; anotar como requisito de design do ADR de observabilidade quando este for escrito (ainda não existe) |

### 4.2 Upload de XSD/pacote malicioso

| ID | Ativo | Ameaça | Vetor | Severidade | Mitigação exigida (bloqueio, não log) | Fase de implementação | Controle de design a refletir agora |
|---|---|---|---|---|---|---|---|
| TM-06 | Processo do parser XSD, filesystem do host | XXE (XML External Entity) | XSD ou pacote contém `<!DOCTYPE>`/`<!ENTITY>` externa que o parser resolve, lendo arquivo local ou fazendo requisição de rede (SSRF) | Crítico | Parser XML configurado com `DtdProcessing.Prohibit` (ou equivalente), resolução de entidades externas desabilitada por padrão, sem opção de reativar via configuração do usuário — falha de parsing por DOCTYPE presente é o comportamento esperado (rejeição, não sanitização silenciosa) | Fase 3 (segurança de importação é entrega explícita da fase) | ADR de formato de template/módulo de origem deve declarar explicitamente "parser XML do núcleo nunca resolve DTD/entidade externa" como restrição de arquitetura, não como detalhe de implementação tardio |
| TM-07 | Filesystem do host de processamento | Path traversal | Nome de entrada dentro do ZIP contém `../`, caminho absoluto, ou link simbólico, permitindo escrita/leitura fora do diretório de extração temporário | Crítico | Normalização e validação de todo nome de entrada antes de qualquer operação de I/O; rejeitar (não sanitizar) entradas com `..`, caminho absoluto, ou fora da raiz esperada; extração sempre para diretório isolado descartável | Fase 3 | Modelo de domínio de "módulo de origem"/"contrato de origem" deve registrar caminho **lógico** (relativo, validado) separado de qualquer caminho físico de filesystem — nunca persistir caminho de filesystem bruto do upload |
| TM-08 | Integridade do schema resolvido | Referência a schema externo fora do pacote | `include`/`import` apontando para URL http(s) externa ou caminho fora do pacote fornecido, tornando a validação dependente de rede/terceiro e não determinística/reproduzível | Crítico | Resolução de `include`/`import` restrita ao conjunto de arquivos do próprio pacote (resolvedor customizado, sem fallback de rede); referência não resolvível dentro do pacote é erro de importação, nunca busca externa | Fase 3 | ADR de versionamento deve exigir que "módulo de origem versionado" seja uma unidade fechada e autocontida (hash do conjunto completo), o que torna referência externa estruturalmente inválida por definição, não só por checagem em runtime |
| TM-09 | Disponibilidade do processo de importação | Zip bomb / excesso de arquivos | ZIP com razão de compressão extrema ou número muito grande de entradas, esgotando memória/disco/CPU durante a extração | Alto | Limites configuráveis e aplicados **antes** de extrair (número máximo de arquivos, tamanho descomprimido máximo, razão de compressão máxima), extração incremental com corte imediato ao violar limite, nunca extração total seguida de checagem | Fase 3 | ADR de formato de template/módulo deve fixar que limites de importação são parâmetros de configuração da plataforma (não hardcoded, não por organização individualmente elevável sem trilha de auditoria) |
| TM-10 | Disponibilidade / armazenamento | Tamanho excessivo de arquivo único ou pacote total | Upload de arquivo muito maior que qualquer XSD legítimo de layout, consumindo banda/memória mesmo antes de tentar interpretar conteúdo | Alto | Limite de tamanho aplicado na borda (antes de carregar o conteúdo inteiro em memória) tanto para arquivo individual quanto para pacote total | Fase 3 | Mesmo ADR de TM-09 deve cobrir tamanho de arquivo individual, não apenas do pacote |
| TM-11 | Determinismo/reprodutibilidade do motor | Colisão ou adulteração de hash de versão | Reimportação de conteúdo alterado sob o mesmo identificador de versão, ou hash calculado sobre subconjunto do conteúdo (não sobre o pacote completo normalizado) | Alto | Hash criptográfico (ex.: SHA-256) sobre o conteúdo completo normalizado do módulo/pacote; versão é imutável — mesma versão nunca aceita conteúdo diferente | Fase 3 | ADR de versionamento deve especificar o algoritmo de hash e o que exatamente compõe a entrada do hash (arquivos + ordem + metadados relevantes) |

### 4.3 Autorização por ação

| ID | Ativo | Ameaça | Vetor | Severidade | Mitigação exigida | Fase de implementação | Controle de design a refletir agora |
|---|---|---|---|---|---|---|---|
| TM-12 | Toda operação sensível (edição, publicação, importação, exportação, diagnóstico, auditoria) | Elevation of Privilege | Introdução de um atalho `IsAdmin`/`IsSuperUser` booleano que, presente, ignora a checagem de permissão granular — comum como "solução temporária" sob pressão de prazo | Crítico | Nenhuma checagem de autorização no código deve testar um papel booleano global; toda checagem deve resolver para uma das 11 permissões por ação (`template.read/create/edit/test/publish/use/archive/import/export`, `diagnostic.capture`, `audit.read`); papel "administrador" inicial é uma **atribuição** que concede todas as permissões explicitamente, nunca um bypass de checagem | Fase 9 (permissões por ação é entrega explícita), mas a superfície da checagem deve nascer certa desde a primeira ADR/modelo de autorização | Modelo de domínio (quando desenhar entidade de permissão/papel) deve modelar "papel" como conjunto nomeado de permissões atômicas, nunca como flag; recomendação para o agente que escrever esse trecho do modelo de domínio: não introduzir `IsAdmin` nem em protótipo/rascunho |
| TM-13 | Motor de geração em produção | Elevation of Privilege | Credencial de runtime (a que o SDK/API usa para gerar XML em produção) recebe, por conveniência operacional, permissões de edição/publicação/importação além de `template.use` | Crítico | Emissão de credencial de runtime restrita, na origem, a `template.use`; validação em dois pontos — na emissão da credencial e em cada chamada de operação de edição, nunca confiar só na primeira | Fase 9 | ADR de contratos de SDK/API (quando escrito) deve distinguir explicitamente "credencial de configuração" de "credencial de runtime" como dois tipos de princípio distintos desde o desenho da fachada, não como parâmetro opcional tardio |
| TM-14 | Ações administrativas (publicar, importar, arquivar) | Repudiation | Ação sensível executada sem geração do evento de auditoria correspondente (falha silenciosa, exceção engolida, ou trilha incompleta) | Alto | Evento de auditoria é efeito obrigatório e transacional da operação (criação, alteração, teste, publicação, rollback, importação, exportação, mudança de permissão, acesso a diagnóstico sensível) — a operação não é considerada bem-sucedida se o registro de auditoria falhar | Fase 9 | Modelo de domínio deve tratar "evento de auditoria" como parte do mesmo caso de uso/transação lógica da operação que o originou, não como efeito colateral best-effort de infraestrutura |
| TM-15 | Permissões concedidas a usuários/credenciais | Elevation of Privilege | Mudança de permissão feita sem trilha de auditoria própria, dificultando detectar concessão indevida retroativamente | Alto | Mudança de permissão é, ela mesma, um evento de auditoria dedicado (já listado na seção 20 do requisito de origem) | Fase 9 | Nenhuma lacuna adicional identificada além de TM-14 |

### 4.4 Vazamento de dado fiscal em log/observabilidade

| ID | Ativo | Ameaça | Vetor | Severidade | Mitigação exigida | Fase de implementação | Controle de design a refletir agora |
|---|---|---|---|---|---|---|---|
| TM-16 | Payload de entrada / XML gerado | Information Disclosure | Log estruturado, atributo de trace (span) ou exceção não tratada inclui o objeto de entrada completo ou o XML final na íntegra (comum em `catch` genérico que loga `ex.ToString()` + contexto completo, ou em middleware de request logging) | Crítico | Logs e traces normais registram apenas metadados (organização, operação, template, versão, duração, cache hit/miss, versões tentadas, fallback, código de erro, resultado, ID de correlação) — nunca o payload ou o XML completo; serialização de exceção deve passar por filtro que remove campos de dado antes de logar | Fase 9 (observabilidade OpenTelemetry é entrega explícita), mas o hábito de não logar payload deve ser regra desde a Fase 2 (primeiro código do motor) | Já coberto por `.claude/rules/security.md`; recomendação: quando o modelo de domínio definir "relatório de validação"/estrutura de diagnóstico (seção 18), separar explicitamente um subtipo "resumo seguro para log" de um subtipo "detalhe completo apenas para captura diagnóstica opt-in", para que a distinção exista no tipo, não só na disciplina do desenvolvedor |
| TM-17 | Captura diagnóstica temporária | Information Disclosure / falha de retenção | Captura opt-in fica acessível sem mascaramento, sem criptografia em repouso, com retenção indefinida, ou sem exclusão automática — vira um log permanente de dado fiscal por outro nome | Crítico | Captura diagnóstica exige: permissão explícita (`diagnostic.capture`), mascaramento de campos sensíveis (documentos fiscais, identificadores pessoais) antes de qualquer persistência, criptografia em repouso, retenção curta com prazo definido e configurável, exclusão automática ao expirar (job/TTL, não processo manual), e geração de evento de auditoria tanto na ativação quanto no acesso posterior à captura | Fase 9 | Nenhum ADR ainda cobre este mecanismo; recomendação: quando a Fase 1 (ou o ADR de observabilidade, ainda não escrito) tratar disso, modelar "captura diagnóstica" como entidade de domínio própria com campos `criadoEm`, `expiraEm`, `mascarada:true`, `criptografada:true`, e não como flag de verbosidade de log |
| TM-18 | Métricas agregadas | Information Disclosure (menor) | Métrica com cardinalidade alta usando valor de dado fiscal como label/dimensão (ex.: usar CNPJ do tomador como label de métrica), inflando cardinalidade e vazando dado por meio do próprio sistema de métricas | Médio | Labels/dimensões de métrica restritos a identificadores de configuração de baixa cardinalidade (organização, operação, template, versão, resultado) — nunca dado do payload | Fase 9 | Nenhuma lacuna adicional; registrar como critério de aceite ao escrever o ADR de observabilidade |
| TM-19 | Trace distribuído (correlação) | Information Disclosure | ID de correlação construído a partir de dado sensível (ex.: hash fraco de CPF) em vez de identificador opaco gerado pela plataforma | Médio | Correlação sempre via identificador opaco gerado no início da operação (ex.: GUID de requisição), nunca derivado de dado fiscal | Fase 9 | Nenhuma lacuna adicional |

### 4.5 Pacote portátil de exportação

| ID | Ativo | Ameaça | Vetor | Severidade | Mitigação exigida | Fase de implementação | Controle de design a refletir agora |
|---|---|---|---|---|---|---|---|
| TM-20 | Certificados/chaves de organização (se armazenados em algum lugar por engano, apesar do limite do núcleo) | Information Disclosure | Pacote de exportação inclui arquivo de certificado/chave que tenha sido anexado a um template/camada por engano (ex.: usuário cola um `.pfx` em um campo de configuração livre, ou um conector externo grava segredo em um campo compartilhado) | Crítico | Validação de conteúdo do pacote **antes** de permitir exportação: nenhum arquivo com extensão/assinatura de certificado ou chave (`.pfx`, `.p12`, `.pem`, `.key`, blocos `BEGIN CERTIFICATE`/`BEGIN PRIVATE KEY` em texto) é permitido no manifesto exportável; a exportação é bloqueada e reportada, não silenciosamente filtrada (filtrar silenciosamente esconderia o problema em vez de forçar correção na origem) | Fase 10 (pacote portátil é entrega explícita), mas a regra "núcleo nunca armazena segredo" já é limite obrigatório desde `CLAUDE.md` | Modelo de domínio deve garantir, desde já, que nenhuma entidade do núcleo (template, mapeamento, regra, camada) tenha um campo de tipo "arquivo livre"/blob não estruturado onde um segredo poderia ser colado; todo campo de configuração deve ter tipo/schema definido |
| TM-21 | Integridade do pacote | Tampering | Pacote exportado é alterado em trânsito ou no armazenamento intermediário antes de ser reimportado em outro ambiente | Alto | Hash do pacote completo registrado no manifesto e verificado na importação; importação rejeita pacote com hash divergente | Fase 10 | ADR de versionamento deve cobrir hash de pacote (não só de módulo de origem individual) |
| TM-22 | Confidencialidade do pacote em trânsito/repouso | Information Disclosure | Pacote contém configuração completa de organização (inclusive regras de negócio proprietárias) e é transportado/armazenado sem controle de acesso equivalente ao do dado de origem | Alto | Pacote exportado herda o mesmo nível de controle de acesso do dado de origem (autorização por `template.export`/`template.import`, auditoria do evento); transporte e armazenamento de pacotes seguem prática padrão de dado sensível de configuração (fora do escopo do núcleo definir o meio de transporte, mas a exigência de controle de acesso é do núcleo) | Fase 10 | Nenhuma lacuna adicional além de TM-20 |

### 4.6 Integridade de publicação/rollback

| ID | Ativo | Ameaça | Vetor | Severidade | Mitigação exigida | Fase de implementação | Controle de design a refletir agora |
|---|---|---|---|---|---|---|---|
| TM-23 | Snapshot publicado ativo (produção) | Tampering | Publicação com camada malformada, testes obrigatórios falhando, ou processo interrompido no meio da materialização do snapshot, resultando em snapshot parcial se tornando o ativo | Crítico | Publicação é atômica: o snapshot novo só se torna "ativo" após materialização completa e validação (estrutural + testes obrigatórios) bem-sucedidas; troca do ponteiro "ativo" é a última operação, nunca escrita incremental sobre o snapshot já servido; falha em qualquer etapa mantém a versão anterior ativa sem alteração (requisito explícito da seção 16: "falha de atualização nunca substitui a versão ativa") | Fase 7 (camadas/versões/publicação) e Fase 8 (gate de publicação com testes obrigatórios) | ADR de formato de template/camada deve modelar snapshot como imutável e versionado, com um ponteiro separado "versão ativa" que só é reatribuído por uma operação atômica dedicada — nunca por mutação do próprio documento de snapshot |
| TM-24 | Histórico de versões / rollback | Tampering / Repudiation | Rollback não disponível, não determinístico, ou não auditado — impossibilita reverter uma publicação problemática ou explicar por que uma versão específica ficou ativa | Alto | Rollback para qualquer versão anterior imutável é sempre possível e gera evento de auditoria explicando a mudança; a resolução de camadas (base → produto → cliente) explica a origem de cada regra mesmo após rollback | Fase 7 | Modelo de domínio de "publicação" deve incluir referência explícita à versão anterior substituída, permitindo reconstruir a cadeia de rollback sem depender só do log de auditoria |
| TM-25 | Atualização automática de camada de cliente | Tampering | Política automática reaplica camada, mas conflito não detectado corretamente publica uma resolução incorreta sem intervenção humana | Alto | Atualização automática só publica se reaplicação + detecção de conflito + testes obrigatórios passarem integralmente; qualquer conflito não resolvível automaticamente cai para fluxo manual (rascunho + diferenças + testes exigidos), nunca publica parcialmente | Fase 7 e Fase 8 (gate de testes obrigatórios) | Nenhuma lacuna adicional além de TM-23/TM-24; a distinção "manual vs. automática" já está na seção 16 e deve ser preservada como dois fluxos de domínio explícitos, não uma flag dentro de um único fluxo |
| TM-26 | Determinismo do fallback interno | Tampering (indireto) | Fallback escolhe uma versão que "parece" compatível mas não foi de fato validada localmente, por falha na ordem de tentativa ou cache de resultado de validação desatualizado | Médio | Fallback só considera versões que geram e validam localmente no momento da tentativa (ou snapshot cujo resultado de validação está associado à mesma versão imutável, nunca a um cache com TTL que possa mentir sobre uma versão diferente) | Fase 7/Fase 11 (resiliência) | ADR de estratégia de cache deve deixar explícito que resultado de validação é atrelado à versão imutável (parte da chave), nunca invalidável por tempo separadamente do conteúdo |

## 5. Lacunas observadas em artefatos paralelos (recomendação, não edição)

Estes pontos foram identificados ao ler o roadmap e os esqueletos existentes,
mas pertencem a arquivos que outros agentes estão escrevendo nesta mesma leva
de tarefas (`docs/domain/`, `docs/architecture/adrs/`, `docs/DECISIONS.md`).
Não foram editados por este documento — são recomendações para quem fechar
esses artefatos:

1. **Modelo de domínio**: ao modelar "organização/tenant" (seção 9), garantir
   que toda entidade raiz de agregado listada no backlog (contrato de origem,
   módulo de origem, template, versão de template, camada de produto/cliente,
   snapshot compilado, publicação, caso de teste, evento de auditoria) tenha
   `OrganizacaoId` não anulável desde a primeira versão do modelo — não
   adicionar depois como migração. Ver TM-01.
2. **Modelo de domínio de permissão/papel**: ao desenhar a entidade de
   autorização, modelar papel como conjunto de permissões atômicas
   (`template.*`, `diagnostic.capture`, `audit.read`) e não introduzir,
   nem como atalho de protótipo, um campo booleano equivalente a `IsAdmin`.
   Ver TM-12.
3. **ADR de persistência (MongoDB)**: quando escrito, declarar explicitamente
   que `organizacaoId` compõe a chave de todo índice relevante junto com
   fornecedor/município/operação/versão/status/template/base/hash (seção 21
   já lista esses campos, mas não menciona organização — a omissão deve ser
   corrigida no ADR). Ver TM-01.
4. **ADR de estratégia de cache**: quando escrito, especificar o formato
   exato da "chave completa" do snapshot em cache incluindo organização como
   componente explícito, e declarar que resultado de validação/fallback é
   atrelado à versão imutável, não invalidável por TTL isolado. Ver TM-02 e
   TM-26.
5. **ADR de formato de template/camada**: quando escrito, declarar que
   nenhum campo de configuração do núcleo aceita blob de arquivo livre sem
   schema (para impedir que segredo seja colado em campo de configuração,
   ver TM-20), e que snapshot publicado é imutável com "versão ativa" como
   ponteiro atômico separado (ver TM-23).

## 6. Limite do núcleo como controle de segurança

A decisão de arquitetura já registrada em `CLAUDE.md` — o núcleo nunca
implementa chamada de rede a prefeitura/fornecedor, autenticação externa,
assinatura digital, criptografia de documento fiscal ou armazenamento de
certificado/chave — é, em si, uma mitigação estrutural: elimina de dentro do
motor e do domínio toda uma classe de ameaças (roubo de credencial de
fornecedor, vazamento de chave privada, SSRF via chamada de emissão) que
teria que ser modelada e defendida se essas responsabilidades estivessem no
núcleo. Qualquer proposta futura de mover essas responsabilidades para
dentro do domínio/motor deve ser tratada como regressão de segurança e
rejeitada nesta camada, redirecionando para um conector externo.

## 7. Especificação de testes de verificação do gate da Fase 9

Não há solução .NET nem framework de teste ainda (a solução nasce na Fase 2,
os testes de segurança formais são entrega da Fase 9 — ver
`docs/ROADMAP.md`). Esta seção fixa, como especificação executável mais
tarde, os dois testes que comprovam o gate "isolamento entre organizações" e
"ausência de payload fiscal em logs normais", para que a Fase 9 não precise
reinterpretar o requisito.

### 7.1 Teste de isolamento entre organizações (cobre TM-01/TM-02/TM-03)

Especificação (a implementar como teste de integração xUnit contra
repositório real, não mock, em `Tradux.Infrastructure.MongoDb.Tests` ou
equivalente quando a solução existir):

```text
Dado um template T-A pertencente à organização A
  e um template T-B pertencente à organização B
  e uma credencial/contexto de execução autenticado como organização A

Quando o repositório é consultado por:
  - listagem de templates sem filtro explícito de ID
  - busca por ID do template T-B usando o contexto da organização A
  - leitura do snapshot em cache logo após geração para a organização B,
    seguida de geração para a organização A com parâmetros que colidiriam
    na chave de cache se a organização não fizesse parte dela

Então:
  - a listagem nunca retorna T-B
  - a busca por ID de T-B sob contexto de organização A falha com "não
    encontrado" (nunca com "não autorizado" revelando existência), pois do
    ponto de vista de A o recurso de B não existe
  - a geração para organização A nunca retorna o snapshot cacheado de B,
    mesmo com template/versão/parâmetros idênticos entre as duas organizações
```

Critério de aceite do gate: este teste (ou conjunto equivalente) existe,
roda em CI, e falha de forma determinística se o filtro de organização for
removido de qualquer camada (repositório ou cache) — ou seja, o teste deve
ser mutation-tested/verificado manualmente uma vez removendo o filtro de
propósito, confirmando que ele de fato pega a regressão.

### 7.2 Teste de ausência de payload fiscal em logs normais (cobre TM-16/TM-18/TM-19)

Especificação (a implementar como teste de integração que captura a saída
real do pipeline de OpenTelemetry — um `InMemoryExporter`/coletor de teste,
não asserção sobre string de código-fonte):

```text
Dado um payload de entrada contendo um valor sentinela único e identificável
  (ex.: um CPF/CNPJ fictício e um texto de descrição de serviço únicos,
  gerados só para este teste, nunca reaproveitados)

Quando uma geração de XML é executada com sucesso
  e também quando a geração falha (erro de validação, mapeamento ausente)
  e também quando uma exceção não tratada ocorre durante a geração

Então, inspecionando todos os logs estruturados, atributos de span/trace e
labels de métrica emitidos para essa execução:
  - nenhum contém o valor sentinela do payload
  - nenhum contém o XML gerado na íntegra
  - os campos esperados (organização, operação, template, versão, duração,
    cache hit/miss, versões tentadas, fallback, código de erro, resultado,
    ID de correlação) estão presentes e são suficientes para diagnosticar
    o caso sem o payload
```

Critério de aceite do gate: este teste cobre os três caminhos (sucesso,
falha esperada, exceção não tratada) — a maioria das violações reais
acontece no caminho de exceção não tratada (`ex.ToString()` genérico), não
no caminho feliz, então cobrir só o caminho de sucesso não fecha o gate.

### 7.3 Teste complementar de captura diagnóstica opt-in (cobre TM-17)

```text
Dado diagnostic.capture concedido para um operador em uma organização

Quando a captura é ativada, uma geração ocorre, e o prazo de retenção expira

Então:
  - a captura registrada contém campos sensíveis mascarados, não em texto
    claro
  - o registro de captura está criptografado em repouso (não legível
    diretamente do armazenamento subjacente sem a chave)
  - existe evento de auditoria tanto na ativação quanto em qualquer leitura
    posterior da captura
  - após o prazo de retenção, a captura deixa de existir sem intervenção
    manual (job de expiração testável isoladamente com relógio controlado)
```

Estes três testes (7.1, 7.2, 7.3), ou seus equivalentes exatos definidos
quando a solução .NET existir, são o gate de saída da Fase 9 e não devem ser
substituídos por inspeção manual de código.
