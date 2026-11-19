# Promoção por pacote portátil

## O que é o pacote

Arquivo `.traduxpkg` (ZIP) de **um template**: manifesto com SHA-256 por
entrada, conteúdo do snapshot (XSDs, mapeamento, tabelas DE-PARA), cenários
funcionais que aprovaram a versão, as versões exatas das camadas e a política
do vínculo. Nunca contém credenciais, chaves, certificados, auditoria ou
captura diagnóstica (a exportação é recusada se detectar segredo).

## Promover de homologação para produção

1. Em homologação: `GET /v1/organizations/{org}/templates/{t}/operations/{op}/package`
   (permissão `template.export`) — ou `TraduxPackages.ExportAsync`.
2. Em produção: `POST /v1/organizations/{org}/packages?templateId={t}&operation={op}`
   com o ZIP no corpo (permissão `template.import`).
3. Confira `hashPreserved: true` no resultado. A versão local pode ter outro
   número; o **hash** é o que prova que é o mesmo template.
4. A importação publica pelos mesmos gates (compilação, testes estruturais,
   cenários do pacote). Recusa → `422` com diagnósticos; a versão ativa não muda.
   O hash do snapshot declarado é conferido **antes** de gravar qualquer coisa
   (`PACKAGE_HASH_NOT_PRESERVED`).
5. Importar exige `template.import`, `template.publish` e `template.create`
   (importar publica e cria camadas).

## Conflitos

- `PACKAGE_LAYER_CONFLICT`: o destino já tem uma camada com o mesmo id e
  versão, mas conteúdo diferente — ambientes divergiram. Resolva publicando
  a versão certa no destino (ou promovendo as camadas antes).
- Camada de **outra organização** (ex.: base compartilhada do fornecedor)
  não vai no pacote (só referência e hash) e precisa existir no destino com
  o mesmo hash; ela nunca é criada pelo pacote.
- Snapshot publicado antes da Fase 10 sem vínculo a camadas não guarda os
  cenários: republique-o (com cenário) antes de exportar, senão a importação
  recusa com `FUNCTIONAL_SCENARIO_REQUIRED`.
- Camadas gravadas por uma importação que depois é recusada ficam no destino
  (camadas são imutáveis e inofensivas sem vínculo); a próxima importação do
  mesmo pacote as reaproveita.
- `PACKAGE_ORGANIZATION_MISMATCH`: pacote com camadas só entra na organização
  de origem. Pacote sem camadas pode ir para qualquer organização.
- `400 Pacote inválido`: hash de entrada não confere, entrada não declarada,
  caminho perigoso, segredo ou limite excedido — o arquivo foi alterado ou
  corrompido; exporte de novo.

## Backup e execução offline

- Guarde os pacotes como backup lógico por template (o backup completo do
  histórico é o do MongoDB).
- Execução sem banco: `var runtime = TraduxPackageRuntime.Load(bytes);` e
  `runtime.GenerateXmlAsync(...)` — o hash é conferido ao carregar; publicar
  não é possível nesse modo.
