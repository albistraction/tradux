# Glossário de domínio

Termos usados de forma consistente em todo o projeto. Atualize sempre que um
termo novo aparecer em requisitos, ADRs ou código, e nunca use um sinônimo não
listado aqui para o mesmo conceito.

## Domínio fiscal (exemplo de uso: NFS-e)

- **NFS-e**: Nota Fiscal de Serviços eletrônica.
- **DPS**: Declaração de Prestação de Serviços — documento de origem enviado
  para gerar uma NFS-e no padrão nacional.
- **RPS**: Recibo Provisório de Serviços — documento de origem em padrões
  municipais anteriores/alternativos ao padrão nacional.
- **Prefeitura / Fornecedor**: entidade externa que recebe/processa o XML.
  Fora do escopo do núcleo (ver limites obrigatórios em `CLAUDE.md`).
- **Município**: escopo geográfico/legal que define o layout de destino
  aplicável.
- **Operação**: tipo de ação representada (emissão, consulta, cancelamento,
  cancelamento por substituição, entre outras — conjunto extensível, não
  enum fechado).

## Modelo de configuração

- **Contrato de origem**: definição dos dados de entrada aceitos (XSD ou
  modelo de objeto configurável), podendo ser composto por múltiplos módulos
  versionados (ex.: `dps@1.x`, `rpAdicional@2.0`).
- **Módulo de origem**: unidade independente e versionada dentro de um
  contrato de origem composto, identificada por nome ou namespace.
- **Schema de destino**: XSD (ou conjunto de XSDs) que define a estrutura do
  XML final a ser gerado e validado.
- **Template**: unidade que associa um contrato de origem a um schema de
  destino através de mapeamentos e regras.
- **Versão de template**: instância imutável de um template em um ponto no
  tempo.
- **Camada de produto**: conjunto de mapeamentos/regras mantido pelo dono do
  produto sobre um template-base de prefeitura.
- **Camada de cliente**: diferenças e extensões que um cliente aplica sobre a
  camada de produto. Armazena apenas o delta, não uma cópia completa.
- **Snapshot compilado / publicado**: materialização completa e imutável da
  resolução de template-base + camada de produto + camada de cliente, pronta
  para execução e cache.
- **Mapeamento**: ligação entre um campo/estrutura de origem e um
  campo/estrutura de destino, com um operador aplicado.
- **Grupo de regras**: agrupamento lógico de condições (`E`, `OU`, negação)
  que controla quando um mapeamento se aplica.
- **Operador**: unidade tipada e extensível do motor de regras (comparação,
  texto, número, data, conversão, coleção, etc. — ver seção 13 do requisito
  de origem para a lista inicial).
- **Bloco de iteração (`for each`)**: mapeamento de uma coleção de origem
  para uma coleção de destino, com filtro, ordenação opcional e mapeamentos
  filhos, podendo aninhar outros blocos de iteração.
- **Caso de teste**: cenário estrutural ou funcional associado a um template,
  usado como gate de publicação.
- **Publicação**: ato de tornar um snapshot ativo para uso em produção.
- **Política de atualização**: regra (manual ou automática) que define como
  uma camada de cliente reage a uma mudança na camada de produto/base.
- **Política de fallback**: ordem e critério de tentativa entre versões
  candidatas de um template na execução.
- **Relatório de validação**: saída estruturada de uma geração, incluindo
  sucesso, XML, versão usada, versões tentadas, erros e diagnósticos.
- **Evento de auditoria**: registro imutável de uma ação sensível (criação,
  edição, teste, publicação, rollback, importação, exportação, mudança de
  permissão, acesso a diagnóstico).

## Arquitetura e execução

- **Plano de configuração**: conjunto de fluxos de import/edição/teste/
  publicação/permissões, tipicamente offline em relação à geração.
- **Plano de execução**: caminho de geração de XML em produção — resolução de
  snapshot, cache, execução de regras, validação, fallback e diagnóstico.
- **Snapshot aquecido / cache aquecido**: estado em que um snapshot compilado
  já está em cache, de forma que a geração não precisa consultar o MongoDB.
- **Runtime sem estado**: instância de execução que não guarda estado
  necessário entre requisições fora do cache, permitindo escala horizontal.
- **Conector externo**: componente fora do núcleo responsável por transporte,
  autenticação, assinatura, criptografia ou orquestração de emissão —
  explicitamente fora do escopo deste projeto.
