---
name: security-review
description: Use antes de fechar qualquer gate de fase que envolva autorização, upload de XSD/pacote, multiempresa, logs ou diagnóstico. Checklist de segurança específico do domínio desta plataforma.
---

# Checklist de revisão de segurança

Verifique cada item e registre o resultado (passou / falhou / não aplicável):

- **Isolamento multiempresa**: todo acesso a dado de template/contrato/
  snapshot é filtrado por organização. Nenhuma consulta cruza organizações
  por engano.
- **Autorização por ação, não por papel booleano**: nenhuma checagem usa
  `IsAdmin` como substituto de permissão granular
  (`template.read/create/edit/test/publish/use/archive/import/export`,
  `diagnostic.capture`, `audit.read`).
- **Credencial de runtime**: uma credencial usada só para gerar XML em
  produção tem, no máximo, `template.use` — nunca permissões de edição.
- **Upload de XSD/pacote**: bloqueia referência externa a schema fora do
  pacote, path traversal em nomes de arquivo, expansão insegura de entidades
  (XXE), quantidade de arquivos e tamanho total acima dos limites
  configurados.
- **Pacote portátil**: nunca contém chaves, certificados ou segredos —
  confirme isso explicitamente antes de qualquer exportação.
- **Logs normais**: não contêm payload de entrada nem XML gerado na íntegra.
- **Diagnóstico sensível**: captura é opt-in, exige permissão explícita,
  aplica mascaramento, é criptografada, tem retenção curta definida, é
  auditada e tem exclusão automática ao expirar.
- **Auditoria**: criação, alteração, teste, publicação, rollback, importação,
  exportação, mudança de permissão e acesso a diagnóstico sensível geram
  evento de auditoria.
- **Limite do núcleo**: nenhuma chamada de rede a prefeitura/fornecedor,
  assinatura digital ou manuseio de certificado aparece dentro do motor ou do
  domínio.

Se algum item falhar, registre como achado crítico com localização exata e
não recomende publicação/merge até a correção.
