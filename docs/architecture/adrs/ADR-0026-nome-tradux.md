# ADR-0026: O projeto passa a se chamar Tradux

- **Status**: aceito
- **Data**: 2026-11-19
- **Fase**: 22 (produto só microsserviço), depois do untangle

## Contexto

Desde o ADR-0025, o produto é um tradutor genérico de XML: XSD de origem → XSD de
destino. A NFS-e foi só o primeiro caso de uso. Mesmo assim, o nome `Nfse.Mapping`
aparecia em namespaces, projetos, configuração, métricas, credenciais e textos, e
amarrava o produto a um domínio que ele não tem mais. O usuário pediu um nome
genérico e escolheu **Tradux** entre as opções propostas.

## Decisão

Todo identificador do projeto que dizia `Nfse`/`nfse`/`NFSE`/"plataforma de DE-PARA
para NFS-e" passa a dizer `Tradux`/`tradux`/`TRADUX`:

| Antes | Depois |
|---|---|
| `Nfse.Mapping.*` (namespaces, projetos, pastas, assemblies) | `Tradux.*` (sem o `.Mapping`) |
| `Nfse.Mapping.slnx` | `Tradux.slnx` |
| `NfseTelemetry`, `AddNfse…`, `MapNfse…` | `TraduxTelemetry`, `AddTradux…`, `MapTradux…` |
| seção de configuração `Nfse:` / `Nfse__` | `Tradux:` / `Tradux__` |
| variáveis `NFSE_*` (`NFSE_API_KEY`, `NFSE_LOAD_SECONDS`, …) | `TRADUX_*` |
| prefixo de credencial `nfse_` (dev: `nfse_dev-admin-org-a`) | `tradux_` (dev: `tradux_dev-admin-org-a`) |
| métricas/atributos `nfse.*` | `tradux.*` |
| banco MongoDB padrão `nfse-mapping` | `tradux-mapping` |
| formato do pacote `nfse-package/1`, extensão `.nfsepkg` | `tradux-package/1`, `.traduxpkg` |
| índice de conhecimento `nfse-knowledge-index/1` | `tradux-knowledge-index/1` |
| rascunho local do editor `nfse-editor-draft-v2` | `tradux-editor-draft-v2` |
| namespaces ilustrativos `http://exemplo.ilustrativo.nfse-engine/…` | `http://exemplo.ilustrativo.tradux/…` |
| agente `domain-nfse-xsd` | `domain-xsd` |
| `docs/requirements/PROMPT_MESTRE_NFSE_CLAUDE.md` | `docs/requirements/PROMPT_MESTRE_CLAUDE.md` |

Ficam como estão, porque são nomes de artefatos externos reais e não o nome deste
projeto: `webserviceNFSe.xsd`, `NFSe_v1.01.xsd`, `nfse_v_1.00.xsd`, o elemento
`NFSe`, `ValorLiquidoNFSe`, `infNFSe`, as classes e pastas do ERP
(`Erp.Serializacao…`, `NfseCustomizadoBase`, `RpsNfse`,
`ConversaoNfseException`, `NfseFactory`), o template publicado
`webservice-nfse-erp` e a pasta local `<pasta-dev>`. As citações de
fato ("pacote NFS-e Nacional 1.01", o glossário do domínio fiscal como exemplo de
uso, o histórico dos ADRs e handoffs) também continuam, porque descrevem o que
aconteceu.

## Alternativas consideradas

- **`Tradux.Mapping.*`**: rejeitada. O `.Mapping` era redundante, já que o produto
  inteiro é o mapeamento.
- **Ler os formatos antigos (`nfse-package/1`, banco `nfse-mapping`, prefixo
  `nfse_`)**: rejeitada. O usuário pediu que nada mais dissesse `nfse`, e ainda não
  há instalação de produção que dependa desses nomes.

## Consequências

- O nome não amarra mais o produto a um domínio fiscal.
- **Quebra de compatibilidade aceita**, sem caminho de leitura do formato antigo:
  - pacotes `.nfsepkg` exportados antes não importam mais;
  - credenciais `nfse_…` gravadas no MongoDB não autenticam mais (o bootstrap de
    Development cria a `tradux_dev-admin-org-a`);
  - o banco padrão mudou. A base de desenvolvimento foi copiada de `nfse-mapping` para
    `tradux-mapping` em 2026-11-19, sem a credencial antiga. Outra instalação precisa
    copiar o banco (`mongodump`/`mongorestore`) ou configurar
    `Tradux:MongoDb:DatabaseName`;
  - variáveis de ambiente e `deploy/.env` precisam usar os nomes `Tradux__*`/`TRADUX_*`;
  - o rascunho local do editor (localStorage) recomeça vazio.
- A pasta raiz do repositório ainda se chama `NFSE_ENGINE`. Renomear essa pasta é
  ação do usuário, fora do repositório.
