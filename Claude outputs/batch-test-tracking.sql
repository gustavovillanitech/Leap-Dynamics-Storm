-- =====================================================================
-- PRUEBA DE LOTE: conversationtrackingid bajo volumen
-- 25 contactos + 25 oportunidades  |  Seattle Storm  |  2026-09-12
-- ANTES DE CORRER: desactiva 'Use TDS Endpoint' (en TDS es solo lectura)
-- =====================================================================

-- ---------- PASO 1: contactos ----------
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('886B8E84-6A67-43DF-8701-48A8CE559A93', 'TestBatch', '01', 'gustavo.villani+tb01@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('6AAE6FEA-E9D0-4DA4-88A4-6D9977E12C92', 'TestBatch', '02', 'gustavo.villani+tb02@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('E8496E36-AB7B-4AC2-AA59-B334A7B7E89C', 'TestBatch', '03', 'gustavo.villani+tb03@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('43EB719C-270B-4306-8815-65BBC9CF3A04', 'TestBatch', '04', 'gustavo.villani+tb04@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('FB507C8B-8DC6-4347-B645-0E4CB72B754F', 'TestBatch', '05', 'gustavo.villani+tb05@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('4539A465-B90F-439D-88E4-D6415FFBC797', 'TestBatch', '06', 'gustavo.villani+tb06@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('DD9F55CE-CAD4-482A-B2EF-FF12909F5173', 'TestBatch', '07', 'gustavo.villani+tb07@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('A39A8411-9539-4EFD-A9A6-D019CA8E4217', 'TestBatch', '08', 'gustavo.villani+tb08@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('17E2FB87-721D-4473-82B2-5D0893AF4705', 'TestBatch', '09', 'gustavo.villani+tb09@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('DBFC70F3-5B52-4CC5-84A9-03BFB871C7BA', 'TestBatch', '10', 'gustavo.villani+tb10@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('BBBCB93F-1816-4344-8667-463FAF8C8F83', 'TestBatch', '11', 'gustavo.villani+tb11@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('55ABAE23-6218-434F-ACC3-A579FEE647C1', 'TestBatch', '12', 'gustavo.villani+tb12@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('7990FA8D-2BB3-47E7-8BCC-F627710C6E9B', 'TestBatch', '13', 'gustavo.villani+tb13@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('B82E24C6-F893-4117-9F8F-869FE329DF18', 'TestBatch', '14', 'gustavo.villani+tb14@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('625EAE50-C9F1-4933-9927-7B6F4B7DCA9F', 'TestBatch', '15', 'gustavo.villani+tb15@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('93A4809B-3D41-4743-A56A-DA826420FE25', 'TestBatch', '16', 'gustavo.villani+tb16@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('2F98CAFA-ED1A-4250-9B21-2227B0887A1E', 'TestBatch', '17', 'gustavo.villani+tb17@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('24353966-17A9-49D5-B36B-9DB91F87C9AB', 'TestBatch', '18', 'gustavo.villani+tb18@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('37EF3EE5-377E-4C44-9DD5-7A452BAA70E0', 'TestBatch', '19', 'gustavo.villani+tb19@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('CDF9F0A3-23FF-410C-9F3B-5FAC8BB7D4D0', 'TestBatch', '20', 'gustavo.villani+tb20@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('991DF799-14E5-4A3E-8F01-0A4850C85BCB', 'TestBatch', '21', 'gustavo.villani+tb21@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('67BDC69B-8B87-4368-B9FE-4B72E40F7E10', 'TestBatch', '22', 'gustavo.villani+tb22@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('BD90351C-EE1C-4608-8F5D-09FA7BBB1D0A', 'TestBatch', '23', 'gustavo.villani+tb23@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('1AE738A6-B2FF-4814-9AD8-3D38B7C31D0D', 'TestBatch', '24', 'gustavo.villani+tb24@leapevent.tech', 100000002);
INSERT INTO contact (contactid, firstname, lastname, emailaddress1, new_contacttype)
VALUES ('825D8CB9-68F9-4611-BEA4-9BD9825A1CA7', 'TestBatch', '25', 'gustavo.villani+tb25@leapevent.tech', 100000002);

-- ---------- PASO 2: oportunidades ----------
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('391B6755-D9DD-43EE-ADC6-2A583FF13FC2', 'TEST batch 01', 100000000, '886B8E84-6A67-43DF-8701-48A8CE559A93', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('2639DD61-BDE0-42DF-95BA-042FE21AFD88', 'TEST batch 02', 100000000, '6AAE6FEA-E9D0-4DA4-88A4-6D9977E12C92', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('FEBDB12D-CD64-47C5-89AC-58ABBC05A0C4', 'TEST batch 03', 100000000, 'E8496E36-AB7B-4AC2-AA59-B334A7B7E89C', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('79783C99-316D-4459-BF79-A4E85A31328E', 'TEST batch 04', 100000000, '43EB719C-270B-4306-8815-65BBC9CF3A04', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('81660AB5-B1D8-4973-94AA-C9F34F6F1099', 'TEST batch 05', 100000000, 'FB507C8B-8DC6-4347-B645-0E4CB72B754F', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('8D75494F-F7B3-472A-9BB0-5152B0C87949', 'TEST batch 06', 100000000, '4539A465-B90F-439D-88E4-D6415FFBC797', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('93BEAA28-2D5B-45BA-B7D5-19DE89D46CF8', 'TEST batch 07', 100000000, 'DD9F55CE-CAD4-482A-B2EF-FF12909F5173', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('8F6841C6-3A87-4FE9-BC00-25C3AE3A8E75', 'TEST batch 08', 100000000, 'A39A8411-9539-4EFD-A9A6-D019CA8E4217', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('CBB4C6F1-3EB1-4948-ADC0-E1EBC9760809', 'TEST batch 09', 100000000, '17E2FB87-721D-4473-82B2-5D0893AF4705', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('1B896CEB-768C-428F-9B7C-64E34AB44156', 'TEST batch 10', 100000000, 'DBFC70F3-5B52-4CC5-84A9-03BFB871C7BA', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('0034E730-9AE2-462A-BB8A-F4CD0C0F1E4A', 'TEST batch 11', 100000000, 'BBBCB93F-1816-4344-8667-463FAF8C8F83', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('4C12D83C-49CD-4B13-807E-23380901B010', 'TEST batch 12', 100000000, '55ABAE23-6218-434F-ACC3-A579FEE647C1', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('238F51F7-0824-4135-9565-00B00C7374A8', 'TEST batch 13', 100000000, '7990FA8D-2BB3-47E7-8BCC-F627710C6E9B', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('58E7CF96-8992-4937-8922-AD3872AD8DE9', 'TEST batch 14', 100000000, 'B82E24C6-F893-4117-9F8F-869FE329DF18', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('5BDA0EFD-9C13-483E-AEFC-83DB689B327B', 'TEST batch 15', 100000000, '625EAE50-C9F1-4933-9927-7B6F4B7DCA9F', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('80612066-2CFA-4EEC-9867-0D9FC1B51AD5', 'TEST batch 16', 100000000, '93A4809B-3D41-4743-A56A-DA826420FE25', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('F510FAB1-435A-47E8-BA18-A9AF2F97A61C', 'TEST batch 17', 100000000, '2F98CAFA-ED1A-4250-9B21-2227B0887A1E', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('9C06BC68-821C-424D-A4F2-F5980DF7DAD1', 'TEST batch 18', 100000000, '24353966-17A9-49D5-B36B-9DB91F87C9AB', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('95ACB0B8-800B-4BA3-BE6F-6F426E155CD0', 'TEST batch 19', 100000000, '37EF3EE5-377E-4C44-9DD5-7A452BAA70E0', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('B37B31FC-C980-49E9-8B45-12B014A251CC', 'TEST batch 20', 100000000, 'CDF9F0A3-23FF-410C-9F3B-5FAC8BB7D4D0', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('2E4D8C9B-992D-4018-8309-FC689812A9A7', 'TEST batch 21', 100000000, '991DF799-14E5-4A3E-8F01-0A4850C85BCB', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('54555AD9-F0E7-4FFF-AF8F-8DD3AF8DE31A', 'TEST batch 22', 100000000, '67BDC69B-8B87-4368-B9FE-4B72E40F7E10', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('F4D7AB11-BCE5-45A6-9C93-0E14CF5D7628', 'TEST batch 23', 100000000, 'BD90351C-EE1C-4608-8F5D-09FA7BBB1D0A', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('1461CA1F-C9B9-4444-B305-599691DA8B78', 'TEST batch 24', 100000000, '1AE738A6-B2FF-4814-9AD8-3D38B7C31D0D', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');
INSERT INTO opportunity (opportunityid, name, new_opportunitytype, parentcontactid, parentaccountid,
                         estimatedclosedate, new_salessource, campaignid, new_season, new_ticketingstage, ownerid)
VALUES ('9E0AB742-B739-490B-B734-ADFBBFFE895A', 'TEST batch 25', 100000000, '825D8CB9-68F9-4611-BEA4-9BD9825A1CA7', '554EBCF2-EC80-E611-80FD-FC15B428AA54',
        '2026-09-30', 100000015, '71434338-771C-E611-80E3-6C3BE5BD3F5C', '2026', 100000000, '015E6826-4FAE-EE11-A569-6045BD0064EB');

-- ---------- PASO 3: verificar que quedaron bien ----------
SELECT name, owneridname, parentcontactidname, new_ticketingstagename
FROM   opportunity
WHERE  name LIKE 'TEST batch %'
ORDER  BY name;

-- ---------- PASO 4 (despues de conectar y enviar): la medicion ----------
-- Que porcentaje de los salientes obtuvo conversationtrackingid
SELECT o.name,
       e.directioncodename,
       e.createdon,
       e.statuscodename,
       e.conversationtrackingid
FROM        email e
INNER JOIN  opportunity o ON o.opportunityid = e.regardingobjectid
WHERE       o.name LIKE 'TEST batch %'
ORDER BY    o.name, e.createdon;

-- ---------- PASO 5: limpieza, cuando termines ----------
-- DELETE FROM opportunity WHERE name LIKE 'TEST batch %';
-- DELETE FROM contact     WHERE emailaddress1 LIKE 'gustavo.villani+tb%@leapevent.tech';