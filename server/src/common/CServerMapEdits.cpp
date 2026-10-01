//
// CServerMapEdits.cpp
//

#include "CServerMapEdits.h"
#include "CLog.h"
#include "sphere_library/CSFile.h"
#include "../sphere/threads.h"

#include <cstring>

CServerMapEdits g_MapEdits;

const char* CServerMapEdits::m_sClassName = "CServerMapEdits";

// File header: "SPHMEDIT" + version.
#define MAPEDITS_MAGIC		"SPHMEDIT"
#define MAPEDITS_MAGIC_LEN	8
#define MAPEDITS_VERSION	3

CServerMapEdits::CServerMapEdits() noexcept
{
	_fDirty = false;
}

const CUOMapBlock* CServerMapEdits::GetBlock(int iMap, dword dwBlockIdx) const noexcept
{
	if ((iMap < 0) || (iMap >= MAP_SUPPORTED_QTY))
		return nullptr;

	const auto& cont = _blocks[iMap];
	if (cont.empty())	// the common case, keep it cheap
		return nullptr;

	const auto it = cont.find(dwBlockIdx);
	if (it == cont.end())
		return nullptr;

	return &(it->second);
}

void CServerMapEdits::SetBlock(int iMap, dword dwBlockIdx, const CUOMapBlock& block)
{
	ADDTOCALLSTACK("CServerMapEdits::SetBlock");
	if ((iMap < 0) || (iMap >= MAP_SUPPORTED_QTY))
		return;

	_blocks[iMap][dwBlockIdx] = block;
	_fDirty = true;
}


const CUOMapBlock* CServerMapEdits::GetOriginalBlock(int iMap, dword dwBlockIdx) const noexcept
{
	if ((iMap < 0) || (iMap >= MAP_SUPPORTED_QTY))
		return nullptr;

	const auto& cont = _originals[iMap];
	if (cont.empty())
		return nullptr;

	const auto it = cont.find(dwBlockIdx);
	if (it == cont.end())
		return nullptr;

	return &(it->second);
}

void CServerMapEdits::SetOriginalBlock(int iMap, dword dwBlockIdx, const CUOMapBlock& block)
{
	ADDTOCALLSTACK("CServerMapEdits::SetOriginalBlock");
	// Only the first time: after that the stored copy is the real mul content, and must not be
	// overwritten with already edited data.
	if ((iMap < 0) || (iMap >= MAP_SUPPORTED_QTY))
		return;

	auto& cont = _originals[iMap];
	if (cont.find(dwBlockIdx) != cont.end())
		return;

	cont[dwBlockIdx] = block;
	_fDirty = true;
}
const CMapOverlayBlock* CServerMapEdits::GetOverlayBlock(int iMap, dword dwBlockIdx) const noexcept
{
	if ((iMap < 0) || (iMap >= MAP_SUPPORTED_QTY))
		return nullptr;

	const auto& cont = _overlays[iMap];
	if (cont.empty())
		return nullptr;

	const auto it = cont.find(dwBlockIdx);
	if (it == cont.end())
		return nullptr;

	return &(it->second);
}

word CServerMapEdits::GetOverlay(int iMap, dword dwBlockIdx, int iIdx) const noexcept
{
	if ((iIdx < 0) || (iIdx >= (UO_BLOCK_SIZE * UO_BLOCK_SIZE)))
		return 0;

	const CMapOverlayBlock* pBlock = GetOverlayBlock(iMap, dwBlockIdx);
	return (pBlock == nullptr) ? (word)0 : pBlock->m_wTile[iIdx];
}

void CServerMapEdits::SetOverlay(int iMap, dword dwBlockIdx, int iIdx, word wTile)
{
	ADDTOCALLSTACK("CServerMapEdits::SetOverlay");
	if ((iMap < 0) || (iMap >= MAP_SUPPORTED_QTY))
		return;
	if ((iIdx < 0) || (iIdx >= (UO_BLOCK_SIZE * UO_BLOCK_SIZE)))
		return;

	auto& cont = _overlays[iMap];
	auto it = cont.find(dwBlockIdx);

	if (it == cont.end())
	{
		if (wTile == 0)		// zaten boş, boş blok açmanın anlamı yok
			return;

		CMapOverlayBlock blockNew{};
		it = cont.emplace(dwBlockIdx, blockNew).first;
	}

	it->second.m_wTile[iIdx] = wTile;
	_fDirty = true;
}

size_t CServerMapEdits::GetEditedBlockCount() const noexcept
{
	size_t uiCount = 0;
	for (int i = 0; i < MAP_SUPPORTED_QTY; ++i)
		uiCount += _blocks[i].size();
	return uiCount;
}

void CServerMapEdits::Clear() noexcept
{
	for (int i = 0; i < MAP_SUPPORTED_QTY; ++i)
	{
		_blocks[i].clear();
		_originals[i].clear();
		_overlays[i].clear();
	}
	_fDirty = true;
}

bool CServerMapEdits::Save(lpctstr ptcFilename)
{
	ADDTOCALLSTACK("CServerMapEdits::Save");

	CSFile file;
	if (!file.Open(ptcFilename, OF_WRITE | OF_BINARY | OF_CREATE))
	{
		g_Log.EventError("Can't open terrain edits file '%s' for writing.\n", ptcFilename);
		return false;
	}

	const dword dwVersion = MAPEDITS_VERSION;
	const dword dwCount = (dword)GetEditedBlockCount();

	if (!file.Write(MAPEDITS_MAGIC, MAPEDITS_MAGIC_LEN) ||
		!file.Write(&dwVersion, sizeof(dwVersion)) ||
		!file.Write(&dwCount, sizeof(dwCount)))
	{
		g_Log.EventError("Error writing terrain edits header to '%s'.\n", ptcFilename);
		return false;
	}

	for (int iMap = 0; iMap < MAP_SUPPORTED_QTY; ++iMap)
	{
		for (const auto& entry : _blocks[iMap])
		{
			const byte bMap = (byte)iMap;
			const dword dwBlockIdx = entry.first;
			if (!file.Write(&bMap, sizeof(bMap)) ||
				!file.Write(&dwBlockIdx, sizeof(dwBlockIdx)) ||
				!file.Write(&(entry.second), sizeof(CUOMapBlock)))
			{
				g_Log.EventError("Error writing terrain edits data to '%s'.\n", ptcFilename);
				return false;
			}
		}
	}

	// The untouched copies, so the editor can still restore a block after a restart.
	dword dwOriginals = 0;
	for (int iMap = 0; iMap < MAP_SUPPORTED_QTY; ++iMap)
		dwOriginals += (dword)_originals[iMap].size();

	if (!file.Write(&dwOriginals, sizeof(dwOriginals)))
	{
		g_Log.EventError("Error writing terrain edits data to '%s'.\n", ptcFilename);
		return false;
	}

	for (int iMap = 0; iMap < MAP_SUPPORTED_QTY; ++iMap)
	{
		for (const auto& entry : _originals[iMap])
		{
			const byte bMap = (byte)iMap;
			const dword dwBlockIdx = entry.first;
			if (!file.Write(&bMap, sizeof(bMap)) ||
				!file.Write(&dwBlockIdx, sizeof(dwBlockIdx)) ||
				!file.Write(&(entry.second), sizeof(CUOMapBlock)))
			{
				g_Log.EventError("Error writing terrain edits data to '%s'.\n", ptcFilename);
				return false;
			}
		}
	}

	// Serpme katmanı (sürüm 3): zeminin üstündeki kum/çakıl grafikleri.
	dword dwOverlays = 0;
	for (int iMap = 0; iMap < MAP_SUPPORTED_QTY; ++iMap)
		dwOverlays += (dword)_overlays[iMap].size();

	if (!file.Write(&dwOverlays, sizeof(dwOverlays)))
	{
		g_Log.EventError("Error writing terrain edits data to '%s'.\n", ptcFilename);
		return false;
	}

	for (int iMap = 0; iMap < MAP_SUPPORTED_QTY; ++iMap)
	{
		for (const auto& entry : _overlays[iMap])
		{
			const byte bMap = (byte)iMap;
			const dword dwBlockIdx = entry.first;
			if (!file.Write(&bMap, sizeof(bMap)) ||
				!file.Write(&dwBlockIdx, sizeof(dwBlockIdx)) ||
				!file.Write(&(entry.second), sizeof(CMapOverlayBlock)))
			{
				g_Log.EventError("Error writing terrain edits data to '%s'.\n", ptcFilename);
				return false;
			}
		}
	}

	file.Close();
	_fDirty = false;
	return true;
}

bool CServerMapEdits::Load(lpctstr ptcFilename)
{
	ADDTOCALLSTACK("CServerMapEdits::Load");

	CSFile file;
	if (!file.Open(ptcFilename, OF_READ | OF_BINARY | OF_NONCRIT))
		return false;	// no edits yet, that's fine

	char szMagic[MAPEDITS_MAGIC_LEN];
	dword dwVersion = 0;
	dword dwCount = 0;

	if ((file.Read(szMagic, MAPEDITS_MAGIC_LEN) != MAPEDITS_MAGIC_LEN) ||
		(memcmp(szMagic, MAPEDITS_MAGIC, MAPEDITS_MAGIC_LEN) != 0) ||
		(file.Read(&dwVersion, sizeof(dwVersion)) != sizeof(dwVersion)) ||
		(file.Read(&dwCount, sizeof(dwCount)) != sizeof(dwCount)))
	{
		g_Log.EventError("Terrain edits file '%s' is corrupted or not a terrain edits file.\n", ptcFilename);
		return false;
	}

	if (dwVersion > MAPEDITS_VERSION)
	{
		g_Log.EventError("Terrain edits file '%s' has an unsupported version (%u).\n", ptcFilename, dwVersion);
		return false;
	}

	Clear();

	for (dword i = 0; i < dwCount; ++i)
	{
		byte bMap = 0;
		dword dwBlockIdx = 0;
		CUOMapBlock block{};

		if ((file.Read(&bMap, sizeof(bMap)) != sizeof(bMap)) ||
			(file.Read(&dwBlockIdx, sizeof(dwBlockIdx)) != sizeof(dwBlockIdx)) ||
			(file.Read(&block, sizeof(block)) != sizeof(block)))
		{
			g_Log.EventError("Terrain edits file '%s' ended early (read %u of %u blocks).\n", ptcFilename, i, dwCount);
			break;
		}

		if (bMap < MAP_SUPPORTED_QTY)
			_blocks[bMap][dwBlockIdx] = block;
	}

	// Version 2 and up also stores the untouched copy of every edited block (used by RESET).
	if (dwVersion >= 2)
	{
		dword dwOriginals = 0;
		if (file.Read(&dwOriginals, sizeof(dwOriginals)) == sizeof(dwOriginals))
		{
			for (dword i = 0; i < dwOriginals; ++i)
			{
				byte bMap = 0;
				dword dwBlockIdx = 0;
				CUOMapBlock block{};

				if ((file.Read(&bMap, sizeof(bMap)) != sizeof(bMap)) ||
					(file.Read(&dwBlockIdx, sizeof(dwBlockIdx)) != sizeof(dwBlockIdx)) ||
					(file.Read(&block, sizeof(block)) != sizeof(block)))
				{
					break;
				}

				if (bMap < MAP_SUPPORTED_QTY)
					_originals[bMap][dwBlockIdx] = block;
			}
		}
	}

	// Sürüm 3 ve üstü serpme katmanını da taşır.
	if (dwVersion >= 3)
	{
		dword dwOverlays = 0;
		if (file.Read(&dwOverlays, sizeof(dwOverlays)) == sizeof(dwOverlays))
		{
			for (dword i = 0; i < dwOverlays; ++i)
			{
				byte bMap = 0;
				dword dwBlockIdx = 0;
				CMapOverlayBlock block{};

				if ((file.Read(&bMap, sizeof(bMap)) != sizeof(bMap)) ||
					(file.Read(&dwBlockIdx, sizeof(dwBlockIdx)) != sizeof(dwBlockIdx)) ||
					(file.Read(&block, sizeof(block)) != sizeof(block)))
				{
					break;
				}

				if (bMap < MAP_SUPPORTED_QTY)
					_overlays[bMap][dwBlockIdx] = block;
			}
		}
	}

	file.Close();
	_fDirty = false;

	const size_t uiLoaded = GetEditedBlockCount();
	if (uiLoaded > 0)
		g_Log.Event(LOGM_INIT, "Loaded %" PRIuSIZE_T " edited terrain block(s) from '%s'.\n", uiLoaded, ptcFilename);

	return true;
}
