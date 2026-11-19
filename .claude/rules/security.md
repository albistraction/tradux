# Regras — Segurança

Aplica-se a autorização, importação de arquivos, logs e diagnóstico em todo o
projeto.

- Autorização é sempre por ação/recurso: `template.read`, `template.create`,
  `template.edit`, `template.test`, `template.publish`, `template.use`,
  `template.archive`, `template.import`, `template.export`,
  `diagnostic.capture`, `audit.read`. Nunca introduza um atalho tipo
  `IsAdmin` que ignore essa lista.
- Toda entidade de domínio relevante (contrato, template, camada, snapshot,
  publicação) é isolada por organização/tenant. Toda query de leitura filtra
  por organização — não confie apenas na UI para isso.
- Credencial de runtime usada para gerar XML em produção tem, no máximo,
  `template.use`.
- Importação de XSD/pacote (único ou ZIP) sempre bloqueia: referência externa
  a schema fora do pacote, path traversal em nomes de entrada, expansão
  insegura de entidades externas (XXE), e limites de quantidade/tamanho de
  arquivo configuráveis.
- Pacote portátil de exportação/importação nunca contém chaves, certificados
  ou segredos. Valide isso antes de qualquer exportação ser permitida.
- Logs e métricas normais não contêm payload de entrada nem XML gerado na
  íntegra. Captura diagnóstica é sempre opt-in, com permissão explícita,
  mascaramento, criptografia, retenção curta e exclusão automática — e ela
  mesma gera evento de auditoria.
- Eventos de auditoria são gerados para: criação, alteração, teste,
  publicação, rollback, importação, exportação, mudança de permissão e acesso
  a diagnóstico sensível.
- O núcleo (domínio + motor) nunca implementa chamada de rede a
  prefeitura/fornecedor, autenticação externa, assinatura digital,
  criptografia de documento ou armazenamento de certificado/chave. Qualquer
  código nessa direção deve ser rejeitado nesta camada e redirecionado para um
  conector externo.
