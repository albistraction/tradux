# Prompt mestre para o Claude Code — Tradux, plataforma configurável de DE-PARA de XML (escrito originalmente para NFS-e)

> Use este arquivo como prompt inicial em um repositório novo. O trabalho é deliberadamente faseado. Não tente entregar o produto inteiro em uma única sessão.

## 1. Missão

Construir uma plataforma multiempresa de tradução de XMLs (o caso de origem é a NFS-e) baseada em XSD, mapeamentos e regras configuráveis.

O núcleo deve ser implementado em C# e disponibilizado de duas formas oficiais:

1. SDK/biblioteca C# instalável no produto consumidor.
2. API HTTP independente em ASP.NET Core.

SDK e API devem usar exatamente o mesmo motor. Não duplique regras ou lógica de geração.

O sistema deve:

- Receber dados de origem em JSON ou objetos C#.
- Carregar contratos de origem compostos e versionados.
- Resolver um template publicado e suas camadas.
- Executar mapeamentos, condições, transformações e iterações.
- Construir o XML diretamente a partir do XSD de destino.
- Validar o XML gerado contra o conjunto correto de XSDs.
- Tentar versões candidatas segundo uma política configurável.
- Retornar o XML, a versão utilizada e um relatório estruturado.

## 2. Limites obrigatórios

O núcleo é responsável apenas por geração e validação de XML.

Não implemente nele:

- Chamadas REST ou SOAP para prefeituras.
- Autenticação em fornecedores.
- Orquestração de emissão síncrona ou assíncrona.
- Polling, consulta de lote ou fluxos particulares de endpoints.
- Assinatura digital ou criptografia.
- Armazenamento de certificados e chaves.

Essas responsabilidades pertencem aos conectores externos. O motor deve oferecer pontos de extensão de pós-processamento, para que o consumidor assine ou criptografe o XML depois da geração.

O fallback interno seleciona uma versão que consiga gerar e validar o XML localmente. Ele não descobre qual versão o endpoint remoto aceita e não deve implementar reenvio remoto.

## 3. Regra de execução para o Claude

Não implemente tudo de uma vez.

Em cada sessão:

1. Leia `CLAUDE.md`.
2. Leia `docs/PROJECT_STATE.md`.
3. Leia `docs/DECISIONS.md` e os ADRs relacionados à fase atual.
4. Leia `docs/ROADMAP.md` e identifique a próxima tarefa desbloqueada.
5. Trabalhe apenas na fase ativa.
6. Use subagentes somente para tarefas independentes, paralelizáveis ou que precisem de contexto isolado.
7. Não permita que dois agentes editem o mesmo conjunto de arquivos simultaneamente.
8. Faça integração, testes e revisão no agente coordenador.
9. Antes de encerrar ou quando o contexto estiver ficando curto, atualize os arquivos de continuidade.
10. Nunca declare uma fase concluída sem cumprir seu gate de saída.

O contexto poderá ser compactado ou uma nova sessão poderá assumir o trabalho. Não encerre o projeto por limite de contexto. Persista o estado no repositório e deixe a próxima ação explícita.

## 4. Estrutura de IA a criar na Fase 0

Crie e mantenha:

```text
CLAUDE.md
.claude/
  agents/
    architect.md
    domain-xsd.md
    dotnet-engine.md
    api-sdk.md
    mongodb-performance.md
    frontend-rule-builder.md
    security-observability.md
    qa-automation.md
    docs-rag.md
    reviewer.md
  skills/
    architecture-adr/SKILL.md
    xsd-contract-analysis/SKILL.md
    rule-operator-design/SKILL.md
    template-migration/SKILL.md
    test-fixture-generation/SKILL.md
    security-review/SKILL.md
    performance-review/SKILL.md
    phase-handoff/SKILL.md
  rules/
    dotnet.md
    testing.md
    security.md
    documentation.md
docs/
  PROJECT_STATE.md
  ROADMAP.md
  DECISIONS.md
  REQUIREMENTS.md
  GLOSSARY.md
  architecture/
    SYSTEM_CONTEXT.md
    CONTAINERS.md
    COMPONENTS.md
    adrs/
  domain/
  api/
  schemas/
  operations/
  runbooks/
  rag/
    INDEX_MANIFEST.md
    SOURCE_REGISTRY.md
    CHUNKING_POLICY.md
    RETRIEVAL_EVALS.md
handoffs/
  CURRENT.md
  archive/
```

Mantenha `CLAUDE.md` curto e focado em regras permanentes, comandos de build/teste, arquitetura essencial e referências aos demais documentos. Coloque conhecimento especializado em skills e regras específicas por caminho.

## 5. Agentes especialistas

### 5.1 Coordenador principal

Responsabilidades:

- Controlar a fase ativa e seu escopo.
- Criar tarefas pequenas e verificáveis.
- Delegar somente trabalhos independentes.
- Evitar conflitos de edição.
- Integrar contribuições.
- Executar o gate da fase.
- Atualizar o estado persistente e o handoff.

O coordenador não deve transferir a responsabilidade final de integração ou aceite.

### 5.2 Arquiteto

Especialidade:

- Clean Architecture.
- DDD pragmático.
- Limites de módulos.
- Contratos públicos.
- ADRs.
- Evolução de monólito modular.

Entregas:

- Diagramas C4/Mermaid.
- Decisões arquiteturais com consequências.
- Revisão de dependências entre projetos.

### 5.3 Especialista em domínio XSD (layouts reais, ex.: NFS-e)

Especialidade:

- XSD, XML Schema, namespaces e XML validation.
- `include`, `import`, `sequence`, `choice`, `all`.
- Cardinalidade, nulabilidade, tipos e restrições.
- Particularidades de layouts fiscais sem misturar transporte ao motor.

Entregas:

- Modelo canônico de schema.
- Casos de borda.
- Fixtures e exemplos de contratos.

### 5.4 Especialista no motor .NET

Especialidade:

- C# e .NET.
- Compilação de templates.
- Motor de regras tipado.
- Geração segura e determinística de XML.
- Cache e concorrência.

Entregas:

- Núcleo sem dependência de web ou banco.
- Operadores extensíveis.
- Benchmarks e testes unitários.

### 5.5 Especialista em API e SDK

Especialidade:

- ASP.NET Core.
- Contratos HTTP.
- SDK público estável.
- Compatibilidade e versionamento.

Entregas:

- Fachadas equivalentes para API e SDK.
- OpenAPI.
- Tratamento padronizado de erros.

### 5.6 Especialista em MongoDB e performance

Especialidade:

- Modelagem documental.
- Índices.
- Snapshots imutáveis.
- Cache e invalidação.
- Testes de carga.

Entregas:

- Modelo de persistência.
- Estratégia de cache.
- Evidências de performance.

### 5.7 Especialista em frontend do editor

Especialidade:

- HTML, CSS e JavaScript simples.
- Árvores de campos.
- Construtor visual de regras.
- Acessibilidade.
- Diagnósticos compreensíveis.

Entregas:

- Editor de DE-PARA.
- Grupos `E`, `OU` e negação.
- Blocos de iteração.
- Tela de testes e publicação.

### 5.8 Especialista em segurança e observabilidade

Especialidade:

- Multiempresa e autorização por recurso.
- Proteção contra upload malicioso.
- Auditoria.
- OpenTelemetry.
- Privacidade de dados fiscais.

Entregas:

- Threat model.
- Controles de segurança.
- Logs, métricas e traces.
- Captura diagnóstica controlada.

### 5.9 Especialista em QA e automação

Especialidade:

- Testes estruturais e funcionais.
- Geração de dados sintéticos.
- Comparação semântica de XML.
- Testes de integração, regressão e carga.

Entregas:

- Estratégia de testes.
- Gates automatizados.
- Matriz de cobertura.

### 5.10 Especialista em documentação e RAG

Especialidade:

- Documentação viva.
- Indexação semântica.
- Chunking e metadados.
- Avaliação de recuperação.
- Handoffs entre sessões.

Entregas:

- Corpus versionado.
- Manifesto de indexação.
- Perguntas de avaliação.
- Runbooks e glossário.

### 5.11 Revisor independente

Responsabilidades:

- Revisar cada gate sem editar a implementação inicialmente.
- Procurar violações de limites, riscos de segurança, regressões e lacunas de teste.
- Emitir achados priorizados e verificáveis.

## 6. Protocolo de continuidade entre agentes e sessões

`docs/PROJECT_STATE.md` deve ser a fonte de verdade operacional e conter:

```text
- fase ativa
- objetivo atual
- concluído
- em andamento
- próximo passo exato
- decisões tomadas
- dúvidas abertas
- riscos e bloqueios
- arquivos alterados
- comandos de build e teste
- último resultado dos testes
- dívida técnica aceita
```

`handoffs/CURRENT.md` deve conter um handoff curto e acionável:

```text
# Handoff atual

## Estado
## O que foi verificado
## Próxima tarefa exata
## Arquivos relevantes
## Comandos para reproduzir
## Riscos
## Não fazer
```

Ao assumir uma sessão nova, o agente deve:

1. Ler os arquivos de estado.
2. Conferir o estado real do repositório.
3. Rodar o teste mínimo relacionado à tarefa.
4. Corrigir divergências entre documentação e realidade.
5. Continuar do próximo passo registrado, sem reiniciar o projeto.

Ao finalizar uma sessão, deve:

1. Atualizar o estado.
2. Registrar decisões estáveis em ADR ou `DECISIONS.md`.
3. Arquivar o handoff anterior quando necessário.
4. Escrever o próximo passo como uma ação concreta.
5. Nunca usar o chat como único local de uma decisão importante.

## 7. Arquitetura do produto

Use:

- ASP.NET Core na versão LTS adotada pelo projeto.
- C#.
- MongoDB por adaptadores de infraestrutura.
- OpenTelemetry.
- HTML, CSS e JavaScript simples no editor inicial.
- Clean Architecture para dependências.
- DDD pragmático no domínio central.
- Monólito modular inicialmente.
- Runtime sem estado e escalável horizontalmente.

Separe:

### Plano de configuração

- Importação de contratos.
- Edição de templates e regras.
- Versionamento e camadas.
- Testes e publicação.
- Permissões e auditoria.
- Importação e exportação.

### Plano de execução

- Resolução do template publicado.
- Cache de snapshot compilado.
- Execução de regras.
- Geração e validação do XML.
- Fallback interno de versões.
- Diagnóstico estruturado.

## 8. Estrutura sugerida da solução

```text
src/
  Tradux.Domain/
  Tradux.Application/
  Tradux.Engine/
  Tradux.Xsd/
  Tradux.Contracts/
  Tradux.Infrastructure.MongoDb/
  Tradux.Infrastructure.Observability/
  Tradux.Sdk/
  Tradux.Api/
  Tradux.Web/

tests/
  Tradux.Domain.Tests/
  Tradux.Engine.Tests/
  Tradux.Xsd.Tests/
  Tradux.Application.Tests/
  Tradux.Api.IntegrationTests/
  Tradux.ArchitectureTests/
  Tradux.PerformanceTests/
```

O domínio e o motor não podem depender de ASP.NET Core, MongoDB ou frontend.

## 9. Modelo de domínio mínimo

Modele:

- Organização/tenant.
- Contrato de origem.
- Módulo de origem.
- Versão de módulo.
- Schema de destino.
- Fornecedor.
- Município.
- Operação.
- Template.
- Versão de template.
- Camada de produto.
- Camada de cliente.
- Mapeamento.
- Grupo de regras.
- Operador.
- Bloco de iteração.
- Caso de teste.
- Publicação.
- Snapshot compilado.
- Política de atualização.
- Política de fallback.
- Relatório de validação.
- Evento de auditoria.

Operações não devem ser uma enumeração rígida. Ofereça modelos iniciais para emissão, consulta, cancelamento e cancelamento por substituição, mas permita novas operações.

## 10. Contratos de origem

Aceite:

- XSD completo ou pacote de XSDs.
- Modelo de objeto configurável, consumido como JSON ou objeto C#.

Normalize tudo em uma árvore interna única e tipada.

Permita contratos compostos por módulos independentes e versionados, por exemplo:

```text
dps@1.x
rpAdicional@2.0
```

Cada módulo deve ter nome ou namespace obrigatório.

A API recebe instâncias em JSON. O SDK aceita JSON e objetos C#, convertendo ambos para a mesma árvore interna.

## 11. Importação segura de XSD

Aceite XSD único e ZIP com múltiplos schemas.

Interprete:

- `include` e `import`.
- Namespaces.
- Tipos simples e complexos.
- Elementos e atributos.
- `sequence`, `choice` e `all`.
- `minOccurs`, `maxOccurs` e `nillable`.
- `default` e `fixed`.
- Enumerações, patterns, limites e tamanhos.

Bloqueie referências externas, path traversal, expansão insegura de entidades, arquivos excessivos e pacotes acima dos limites configurados.

Versione o conjunto e gere hash do conteúdo. Não trate “opcional” e “aceita nulo” como equivalentes.

## 12. Editor visual

Crie uma experiência semelhante ao construtor de consultas do Azure DevOps.

O usuário deve conseguir:

- Navegar nas árvores de origem e destino.
- Criar o DE-PARA por seleção ou arraste.
- Configurar campo, operador e valor.
- Agrupar condições com `E`, `OU` e negação.
- Definir constantes, defaults, transformações e omissão.
- Criar blocos `for each` com filtros e listas aninhadas.
- Visualizar obrigatórios sem mapeamento.
- Visualizar erros de tipo e cardinalidade.
- Testar com dados de exemplo.
- Comparar o XML gerado.
- Publicar somente versões válidas.

Não permita JavaScript ou C# arbitrário escrito pelo usuário.

## 13. Motor de regras

Implemente catálogo tipado e extensível de operadores C#.

Categorias:

- Comparação.
- Existência e nulidade.
- Lógica booleana.
- Texto.
- Números.
- Datas.
- Conversão.
- Constantes e defaults.
- Tabelas DE-PARA.
- Coleções, filtros e agregações.
- Formatação.
- Omissão de nós.

Operadores iniciais:

```text
equals, notEquals, greaterThan, lessThan,
isEmpty, notEmpty, and, or, not,
copy, constant, coalesce, concat,
substring, replace, trim, padLeft, padRight,
formatDate, formatNumber, lookup,
omit, setNull, setDefault
```

Cada operador declara tipos aceitos, retorno, parâmetros, validações, códigos de erro e compatibilidade com coleções.

Novos operadores devem ser adicionáveis por desenvolvedores sem modificar o núcleo.

## 14. Coleções

Implemente blocos de iteração com:

- Caminho da coleção de origem.
- Contexto local do item.
- Índice.
- Filtro.
- Ordenação opcional.
- Mapeamentos filhos.
- Listas aninhadas.
- Geração de zero, um ou vários nós.

Valide o resultado contra `minOccurs` e `maxOccurs`.

## 15. Geração do XML

Não use XML-base fixo e não concatene strings.

Construa o documento a partir do modelo compilado do XSD, garantindo:

- Ordem dos elementos.
- Namespaces e prefixos consistentes.
- Atributos.
- Obrigatórios.
- Omissão de opcionais.
- Nulabilidade.
- Repetições.
- Ramos de `choice`.
- Conversão invariante à cultura.
- UTF-8 como padrão, com codificação configurável.

Valide o XML final com os schemas exatos da versão do template.

## 16. Camadas, publicação e atualização

Resolva:

```text
template-base da prefeitura
        ↓
camada de produto
        ↓
camada de cliente
        ↓
snapshot publicado
```

Regras:

- Base e produto são protegidos.
- Cliente armazena somente diferenças e extensões.
- Publicação materializa snapshot completo e imutável.
- A resolução deve explicar a origem de cada regra.
- Deve existir rollback.

Política por cliente:

- Manual: cria rascunho, mostra diferenças e conflitos e exige testes.
- Automática: reaplica a camada, detecta conflitos, executa testes e publica apenas se tudo passar.

Falha de atualização nunca substitui a versão ativa.

## 17. Versionamento e fallback

Mantenha versões imutáveis de contratos, schemas, templates, camadas e snapshots.

Na execução:

1. Leia a lista ordenada de versões candidatas.
2. Tente a mais nova.
3. Execute regras e gere o XML.
4. Valide internamente.
5. Diante de erro crítico, tente a próxima.
6. Use a primeira válida.
7. Se todas falharem, retorne erros agrupados por versão.

## 18. Erros

Níveis:

- Informação.
- Aviso.
- Crítico.

Falhas de XSD, conversão, cardinalidade, compilação e execução são sempre críticas. Validações de negócio podem ter severidade configurável quando permitido.

O resultado deve incluir sucesso, XML quando válido, versão usada, versões tentadas, código estável, mensagem, origem, XPath de destino, regra responsável, sugestão e duração.

## 19. Testes obrigatórios

### Estruturais automáticos

Gere dados sintéticos a partir dos contratos e cubra:

- Apenas obrigatórios.
- Obrigatórios e opcionais.
- Limites e tamanhos.
- Enumerações e patterns.
- Repetições.
- Cada ramo de `choice`.
- Nulos permitidos.
- Casos inválidos esperados.

Evite explosão combinatória; use cobertura determinística.

### Funcionais

Exija ao menos um cenário criado pelo autor, com entrada e XML esperado ou asserções estruturais.

Compare XML semanticamente, ignorando diferenças irrelevantes de prefixo e formatação.

Nenhum template pode ser publicado se os testes obrigatórios falharem.

## 20. Segurança e autorização

Implemente isolamento por organização e permissões por ação:

```text
template.read
template.create
template.edit
template.test
template.publish
template.use
template.archive
template.import
template.export
diagnostic.capture
audit.read
```

Comece com um papel administrador, sem acoplar autorização a um booleano `IsAdmin`. Credenciais de runtime podem ter somente `template.use`.

Audite criação, alteração, teste, publicação, rollback, importação, exportação, permissão e acesso a diagnóstico sensível.

## 21. Persistência e cache

Use MongoDB somente por interfaces de repositório.

Considere índices por organização, fornecedor, município, operação, versão, status, template, base e hash.

O caminho normal de geração não deve consultar MongoDB com cache aquecido.

Use snapshots compilados em cache, com chave completa, invalidação atômica e proteção contra atualização parcial.

## 22. Pacote portátil

Defina um pacote importável e exportável com:

- Manifesto.
- Contratos de origem.
- XSDs e dependências.
- Mapas e regras.
- Camadas.
- Testes.
- Metadados de versão.
- Hashes.

Use-o para promoção entre ambientes, backup, execução offline da DLL e compartilhamento controlado.

Valide integridade, compatibilidade, limites e conteúdo. Nunca inclua chaves, certificados ou segredos.

## 23. Observabilidade e diagnóstico

Implemente logs estruturados, métricas e tracing com OpenTelemetry.

Registre organização, operação, template, versão, duração, cache, versões tentadas, fallback, erros, resultado e correlação.

Não registre payload ou XML integral normalmente.

Permita captura diagnóstica temporária e opt-in, com permissão, mascaramento, criptografia, retenção curta, auditoria e exclusão automática.

## 24. RAG e documentação viva

O RAG é apoio à engenharia e ao editor; não participa da geração determinística do XML em produção.

Indexe somente fontes aprovadas e versionadas:

- Requisitos.
- ADRs.
- Glossário.
- Documentação de domínio.
- XSDs e documentação associada.
- Exemplos aprovados.
- Catálogo de operadores.
- OpenAPI.
- Runbooks.
- Casos de teste publicados.

Cada chunk deve conter metadados como:

```text
source_id
source_type
version
tenant_scope
provider
municipality
operation
schema_version
language
content_hash
effective_date
```

Requisitos do RAG:

- Isolamento por tenant e nível de acesso.
- Referência à fonte e versão em toda resposta.
- Reindexação incremental por hash.
- Remoção de chunks obsoletos.
- Política explícita de chunking por tipo documental.
- Conjunto de perguntas de avaliação com respostas e fontes esperadas.
- Métricas de precisão de recuperação e cobertura.
- Proibição de usar respostas do RAG como regra executável sem publicação formal.

Comece simples: interface de indexação, armazenamento vetorial substituível e implementação inicial adequada ao ambiente disponível. Registre em ADR a escolha concreta antes de adotar um produto.

## 25. Performance

Dimensione para 100 a 200 gerações por minuto e teste com margem de dez vezes o pico.

Metas:

- Nenhuma leitura do MongoDB com cache aquecido.
- Runtime sem estado.
- Templates e XSDs pré-compilados.
- Escala horizontal.
- Limites de tempo, memória, profundidade e entrada.
- Benchmarks separados para compilação e execução.
- Métricas de percentis.

Não introduza filas ou microserviços sem evidência.

## 26. API e SDK

Forneça fachadas equivalentes para:

- Importar contrato.
- Criar template e versão.
- Criar camada.
- Validar rascunho.
- Executar testes.
- Publicar.
- Fazer rollback.
- Gerar XML.
- Listar e comparar versões.
- Importar e exportar pacote.

A geração recebe organização, template, operação, dados, contexto opcional, política de versão e correlação.

Retorna XML válido, versão usada, relatório, avisos e metadados.

## 27. Fases de implementação

### Fase 0 — Bootstrap de IA e governança

Objetivo: tornar o projeto retomável por qualquer nova sessão.

Entregas:

- Estrutura `.claude`.
- Agentes e skills.
- `CLAUDE.md`.
- Estado, roadmap, glossário e handoff.
- Templates de ADR.
- Convenções de código, teste e documentação.
- Backlog inicial por fases.

Gate:

- Uma nova sessão consegue explicar o projeto, localizar o estado e indicar a próxima tarefa somente lendo os arquivos persistidos.

### Fase 1 — Descoberta técnica e arquitetura executável

Objetivo: fechar contratos e riscos antes de codificar o motor.

Entregas:

- Diagramas de contexto, containers e componentes.
- Modelo de domínio.
- ADRs de arquitetura, versionamento, persistência, cache e formato de template.
- Contratos públicos preliminares de SDK e API.
- Threat model inicial.
- Fixtures representativas de XSD.

Gate:

- Nenhuma dependência de infraestrutura invade domínio ou motor.
- Limites e formatos críticos estão documentados e revisados.

### Fase 2 — Vertical slice do motor

Objetivo: provar o fluxo ponta a ponta mais simples.

Entregas:

- Importação de XSD único.
- Árvore interna tipada.
- Mapeamento direto de campos escalares.
- Geração ordenada de XML.
- Validação por XSD.
- SDK C# mínimo.
- Testes unitários e exemplo realista.

Gate:

- Um objeto de entrada gera XML válido sem strings concatenadas e com diagnóstico útil.

### Fase 3 — XSD completo e contratos compostos

Objetivo: suportar layouts fiscais reais.

Entregas:

- ZIP, `include` e `import`.
- Namespaces.
- Tipos, restrições, atributos e cardinalidade.
- `sequence`, `choice` e `all`.
- Módulos de origem versionados e namespaced.
- Segurança de importação.

Gate:

- Pacotes complexos são importados de forma determinística, segura e reproduzível.

### Fase 4 — Motor de regras e coleções

Objetivo: implementar a flexibilidade do DE-PARA.

Entregas:

- Catálogo tipado de operadores.
- Grupos lógicos.
- Conversões e lookups.
- `for each`, filtros e listas aninhadas.
- Extensão segura por novos operadores C#.
- Diagnóstico por regra e caminho.

Gate:

- Cenários escalares e de coleções geram XML correto e nenhum código arbitrário é executado.

### Fase 5 — API, MongoDB, cache e SDK público

Objetivo: disponibilizar o motor nos dois formatos oficiais.

Entregas:

- API ASP.NET Core.
- MongoDB por adaptadores.
- Snapshots compilados.
- Cache e invalidação atômica.
- SDK empacotável.
- OpenAPI.
- Testes de integração.

Gate:

- API e SDK produzem resultados equivalentes.
- Execução com cache aquecido não acessa MongoDB.

### Fase 6 — Editor visual

Objetivo: permitir configuração por usuários autorizados.

Entregas:

- Árvores de origem e destino.
- Editor de mapeamentos.
- Query builder de regras.
- Blocos de iteração.
- Diagnósticos visuais.
- Execução de testes.

Gate:

- Um usuário cria e testa um template sem editar JSON manualmente.

### Fase 7 — Camadas, versões e publicação

Objetivo: suportar produto, cliente e evolução segura.

Entregas:

- Base, produto e cliente.
- Diferenças rastreáveis.
- Snapshot imutável.
- Atualização manual e automática.
- Conflitos, rollback e fallback.
- Auditoria.

Gate:

- Uma atualização incompatível não altera a versão ativa.
- Toda decisão de resolução pode ser explicada.

### Fase 8 — Qualidade automatizada

Objetivo: impedir publicação de templates estrutural ou semanticamente inválidos.

Entregas:

- Gerador de dados sintéticos.
- Testes estruturais automáticos.
- Testes funcionais obrigatórios.
- Comparação semântica de XML.
- Gate de publicação.
- Regressão em atualização de camadas.

Gate:

- Não é possível publicar com teste obrigatório falhando.

### Fase 9 — Segurança, autorização e observabilidade

Objetivo: preparar uso multiempresa em produção.

Entregas:

- Isolamento por tenant.
- Permissões por ação.
- Credencial runtime somente uso.
- OpenTelemetry.
- Auditoria.
- Captura diagnóstica segura.
- Testes de segurança.

Gate:

- Testes comprovam isolamento e ausência de payload fiscal em logs normais.

### Fase 10 — Pacotes, RAG e documentação operacional

Objetivo: facilitar promoção, suporte e continuidade do conhecimento.

Entregas:

- Importação e exportação portável.
- Corpus documental.
- Pipeline incremental de indexação.
- Controle de acesso na recuperação.
- Avaliações do RAG.
- Runbooks.
- Documentação para consumidores do SDK e API.

Gate:

- Um pacote passa entre ambientes com hashes preservados.
- O RAG recupera fontes corretas e informa versão, sem interferir no runtime determinístico.

### Fase 11 — Performance, resiliência e prontidão

Objetivo: validar a operação empresarial.

Entregas:

- Teste a 200 gerações/minuto.
- Teste de margem a 2.000/minuto.
- Percentis, CPU, memória e cache.
- Testes de concorrência e invalidação.
- Runbooks de falha e rollback.
- Revisão final de arquitetura, segurança e documentação.

Gate:

- Metas atingidas ou gargalos medidos e aceitos formalmente.
- Operação e recuperação estão documentadas.

## 28. Critérios globais de aceite

- SDK e API usam o mesmo motor.
- XSD único e pacotes relacionados são importados com segurança.
- XML é construído a partir do schema, sem concatenação de strings.
- Obrigatórios sem mapeamento são detectados antes da publicação.
- Regras são tipadas e sem código arbitrário.
- Listas aninhadas funcionam.
- Camadas são rastreáveis.
- Atualização falha não substitui versão ativa.
- Todo XML liberado passa pelo XSD correto.
- Fallback informa erros por versão.
- Publicações são imutáveis e reversíveis.
- Testes estruturais e funcionais são obrigatórios.
- Runtime aquecido não depende de MongoDB por geração.
- Dados fiscais não vazam em logs.
- O núcleo não depende de REST, SOAP, certificados ou particularidades de fornecedor.
- A documentação e o handoff permitem continuidade por outro agente.

## 29. Primeira execução esperada

Ao receber este prompt:

1. Não comece a implementar o produto inteiro.
2. Execute apenas a Fase 0.
3. Mostre a estrutura proposta de agentes e skills antes de criá-la se houver decisão material ainda aberta.
4. Crie os arquivos de continuidade.
5. Registre este documento como requisito de origem.
6. Produza o backlog detalhado da Fase 1.
7. Rode uma revisão independente da Fase 0.
8. Encerre informando o gate, os arquivos criados e a próxima tarefa exata.

Depois, prossiga uma fase por vez, sempre respeitando gates, documentação e handoffs.

## 30. Referências oficiais do Claude Code

- Estrutura de `CLAUDE.md`, regras, skills, agentes e memória: https://code.claude.com/docs/en/claude-directory
- Visão geral de skills, subagentes, agent teams e quando usar cada recurso: https://code.claude.com/docs/en/features-overview
- Continuação e retomada de sessões pela CLI: https://docs.anthropic.com/en/docs/claude-code/cli-usage

