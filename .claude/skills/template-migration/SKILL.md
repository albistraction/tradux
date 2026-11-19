---
name: template-migration
description: Use ao evoluir uma camada (produto ou cliente) sobre um template base, ou ao propagar uma atualização de camada de produto para clientes. Garante rastreabilidade, testes e que a versão ativa nunca é substituída por uma atualização que falha.
---

# Migrar/atualizar uma camada de template

1. Identifique a cadeia de resolução envolvida: template-base da prefeitura →
   camada de produto → camada de cliente → snapshot publicado. Confirme qual
   camada está sendo alterada.
2. Lembre-se: base e produto são protegidos (só o dono da camada correspondente
   edita); cliente armazena somente diferenças e extensões sobre a camada
   acima.
3. Antes de aplicar a atualização, gere um diff explícito das mudanças e
   identifique conflitos com customizações de camadas abaixo na cadeia.
4. Escolha a política aplicável ao cliente:
   - **Manual**: cria rascunho, mostra diferenças e conflitos, exige testes
     antes de qualquer publicação.
   - **Automática**: reaplica a camada, detecta conflitos, executa os testes
     obrigatórios e só publica se tudo passar.
5. Regra inegociável: uma falha de atualização (conflito não resolvido ou
   teste obrigatório falhando) nunca substitui a versão ativa em produção.
6. Ao publicar, materialize um snapshot completo e imutável, e garanta que
   existe caminho de rollback para a versão anterior.
7. Registre em log/auditoria quem disparou a atualização, o resultado, e — se
   aplicável — o motivo do rollback.
8. Documente a origem de cada regra resolvida (de qual camada ela veio) para
   que a resolução seja explicável a qualquer momento.
